// Services/UploadService.cs
using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using ExcelDataReader;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using UploadData.Models; // 👈 modelo UploadResult

namespace UploadData.src.Services
{
    public class UploadService : IUploadService
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<UploadService> _logger;

        private readonly string _conn;
        private readonly string _destTable;
        private readonly string _periodCol;
        private readonly bool _bulkByName;
        private readonly long _maxFileSize;
        private readonly IDictionary<string, string> _colMap;
        private readonly string[] _requiredHeaders;
        private readonly string[] _ignoreCols;

        // Post-load (opcional)
        private readonly bool _runPostLoad;
        private readonly string _postLoadProc;

        public UploadService(IConfiguration cfg, ILogger<UploadService> logger)
        {
            _cfg = cfg;
            _logger = logger;

            // ---- Upload settings ----
            var up = _cfg.GetSection("Upload");
            var connName = up.GetValue<string>("ConnectionName") ?? "AcademicDb";

            _conn         = _cfg.GetConnectionString(connName)
                            ?? throw new InvalidOperationException($"Falta ConnectionStrings:{connName} en appsettings.");
            _destTable    = up.GetValue<string>("DestinationTable") ?? "STG.ofertas_raw";
            _periodCol    = up.GetValue<string>("PeriodColumn") ?? "per_codigo";
            _bulkByName   = up.GetValue<bool>("BulkByColumnName", true);
            _maxFileSize  = up.GetValue<long>("MaxFileSizeBytes", 200_000_000);

            _colMap = up.GetSection("ColumnMap")
                        .GetChildren()
                        .ToDictionary(x => x.Key, x => x.Value ?? x.Key, StringComparer.OrdinalIgnoreCase);

            _requiredHeaders = up.GetSection("RequiredHeaders").Get<string[]>() ?? Array.Empty<string>();
            _ignoreCols      = up.GetSection("IgnoreColumns").Get<string[]>() ?? Array.Empty<string>();

            _runPostLoad  = up.GetValue<bool>("RunPostLoad", false);
            _postLoadProc = up.GetValue<string>("PostLoadProcedure") ?? "STG.p_ofertas_raw_postload";
        }

        // ==========================================================
        // Excel -> STG.ofertas_raw
        // ==========================================================
        public async Task<UploadResult> ProcessExcelToStagingAsync(
            string filePath, string? sheet, int? semestre, CancellationToken ct)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var fi = new FileInfo(filePath);
            if (!fi.Exists) throw new FileNotFoundException("No se encontró el archivo", filePath);
            if (fi.Length > _maxFileSize) throw new InvalidOperationException("Archivo demasiado grande.");

            using var fs = File.OpenRead(filePath);
            using var reader = ExcelReaderFactory.CreateReader(fs);

            var ds = reader.AsDataSet(new ExcelDataSetConfiguration
            {
                ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = true }
            });

            DataTable dt =
                (!string.IsNullOrWhiteSpace(sheet) && ds.Tables.Contains(sheet))
                    ? ds.Tables[sheet]!
                    : ds.Tables[0];

            // 1) Normaliza encabezados
            foreach (DataColumn c in dt.Columns) c.ColumnName = c.ColumnName.Trim();

            // 2) Quitar columnas a ignorar
            foreach (var ign in _ignoreCols.Where(dt.Columns.Contains).ToList())
                dt.Columns.Remove(ign);

            // 3) Validar requeridos (solo que existan en cabecera)
            var missing = _requiredHeaders.Where(h => !dt.Columns.Contains(h)).ToList();
            if (missing.Any())
                throw new InvalidOperationException($"Faltan encabezados requeridos: {string.Join(", ", missing)}");

            // 4) Mapear Excel -> Destino según ColumnMap
            foreach (var kv in _colMap)
            {
                var excelCol = kv.Key;      // en Excel
                var destCol  = kv.Value;    // en STG
                if (!dt.Columns.Contains(excelCol)) continue;

                if (!dt.Columns.Contains(destCol)) dt.Columns.Add(destCol, typeof(string));
                foreach (DataRow r in dt.Rows) r[destCol] = r[excelCol];
            }

            // 5) Normalizar hora_inicio a HH:mm (si existe)
            ToTime(dt, "hora_inicio");

            // 6) Metadatos de carga (un batch por archivo)
            var batchId  = Guid.NewGuid();
            var loadedAt = DateTime.UtcNow;
            var srcFile  = Path.GetFileName(filePath);

            EnsureColumnsForStaging(dt); // agrega/ajusta tipos de source_file, loaded_at, batch_id

            foreach (DataRow r in dt.Rows)
            {
                r["source_file"] = srcFile;
                r["loaded_at"]   = loadedAt;
                r["batch_id"]    = batchId;
            }

            // 7) Filtrar filas que tengan PeriodColumn (per_codigo)
            var keep = KeepRowsWithRequired(dt, _periodCol);
            var rowsOk      = keep.rowsOk;
            var dropped     = keep.rowsDropped;
            var sampleWhy   = keep.sampleWhy;

            _logger.LogInformation("STG upload: válidas={ok}, descartadas={drop}. Ejemplo motivo: {why}",
                rowsOk.Rows.Count, dropped, sampleWhy);

            // 8) Bulk insert + delete por periodo
            int rowsRead     = dt.Rows.Count;
            int rowsInserted = 0;

            if (rowsOk.Rows.Count > 0)
            {
                rowsInserted = await CargarDataTableAsync(rowsOk, replacePeriod: true);

                // Post-carga opcional en SQL (derivaciones/normalizaciones)
                if (_runPostLoad && rowsInserted > 0)
                {
                    try
                    {
                        await RunPostLoadAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Falló post-load '{proc}'. Se continúa.", _postLoadProc);
                    }
                }
            }
            else
            {
                _logger.LogWarning("No hay filas válidas para insertar.");
            }

            // 9) Construir resultado enriquecido
            var result = new UploadResult
            {
                BatchId   = batchId,
                Inserted  = rowsInserted,
                FileName  = srcFile,
                Worksheet = dt.TableName,
                Message   = $"Leídas: {rowsRead}, insertadas: {rowsInserted}, descartadas: {dropped}.",
                Warnings  = new List<string>()
            };

            if (!string.IsNullOrWhiteSpace(sampleWhy))
                result.Warnings.Add($"Ejemplo de descarte: {sampleWhy}");

            return result;
        }

        // ==========================================================
        // CSV -> STG.ofertas_raw (opcional)
        // ==========================================================
        public async Task<int> CargarCsvAsync(string csvPath, bool replacePeriod = false)
        {
            if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
                throw new FileNotFoundException("No se encontró el archivo CSV", csvPath);

            var dt = CsvToDataTable(csvPath);
            EnsureColumnsForStaging(dt);

            // Normaliza hora si viene la columna
            ToTime(dt, "hora_inicio");

            return await CargarDataTableAsync(dt, replacePeriod);
        }

        // ==========================================================
        // Bulk Copy
        // ==========================================================
        public async Task<int> CargarDataTableAsync(DataTable dt, bool replacePeriod = true)
        {
            if (dt.Rows.Count == 0) { _logger.LogWarning("DataTable vacío."); return 0; }

            using var cn = new SqlConnection(_conn);
            await cn.OpenAsync();

            // Borrar por periodo (si corresponde)
            if (replacePeriod && dt.Columns.Contains(_periodCol))
            {
                var periods = dt.AsEnumerable()
                                .Select(r => r[_periodCol]?.ToString())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .Distinct()
                                .ToArray();

                if (periods.Length > 0)
                {
                    using var del = new SqlCommand(
                        $"DELETE FROM {_destTable} WHERE {_periodCol} IN ({string.Join(",", periods.Select((_, i) => $"@p{i}"))})",
                        cn);

                    for (int i = 0; i < periods.Length; i++)
                        del.Parameters.AddWithValue($"@p{i}", periods[i]!);

                    var deleted = await del.ExecuteNonQueryAsync();
                    _logger.LogInformation("Eliminadas {count} filas previas de {table}.", deleted, _destTable);
                }
            }

            using var bulk = new SqlBulkCopy(cn)
            {
                DestinationTableName = _destTable,
                BulkCopyTimeout = 0
            };

            // 🎯 Seguridad: mapea SOLO columnas válidas del destino
            string[] allowedDestCols = {
                "per_codigo","mod_codigo","cam_codigo","sec_codigo","doc_codigo","nombre_materia",
                "ofe_modulo","ofe_semestre","ofe_anio","ofe_nivel",
                "ofe_presencialidad_obligatoria","ofe_duracion_clase","ofe_es_core",
                "hora_inicio","hora_fin",
                "dias_pattern","bloque_horario","is_virtual",
                "dia_L","dia_MA","dia_MI","dia_J","dia_V","dia_S","dia_D",
                "pre_solicitudes","ofe_modalidad_programa","ofe_matriculados",
                "source_file","loaded_at","batch_id"
            };

            foreach (var col in allowedDestCols)
                if (dt.Columns.Contains(col))
                    bulk.ColumnMappings.Add(col, col);

            await bulk.WriteToServerAsync(dt);
            return dt.Rows.Count;
        }

        // ==========================================================
        // Post-load (SP en SQL)
        // ==========================================================
        public async Task RunPostLoadAsync(CancellationToken ct)
        {
            if (!_runPostLoad) return;

            using var cn = new SqlConnection(_conn);
            await cn.OpenAsync(ct);

            using var cmd = new SqlCommand(_postLoadProc, cn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 0
            };

            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Post-load ejecutado: {proc}", _postLoadProc);
        }

        // ==========================================================
        // Helpers
        // ==========================================================
        private static void EnsureColumnsForStaging(DataTable dt)
        {
            // Solo metadatos; el resto de derivaciones se hacen en SQL post-carga
            if (!dt.Columns.Contains("source_file")) dt.Columns.Add("source_file", typeof(string));
            if (!dt.Columns.Contains("loaded_at"))   dt.Columns.Add("loaded_at",   typeof(DateTime));
            if (!dt.Columns.Contains("batch_id"))    dt.Columns.Add("batch_id",    typeof(Guid));

            // Asegurar tipos correctos
            EnsureOrChangeColumnType(dt, "loaded_at", typeof(DateTime));
            EnsureOrChangeColumnType(dt, "batch_id",  typeof(Guid));
        }

        private static void EnsureOrChangeColumnType(DataTable dt, string col, Type targetType)
        {
            if (!dt.Columns.Contains(col))
            {
                dt.Columns.Add(col, targetType);
                return;
            }

            var c = dt.Columns[col];
            if (c.DataType == targetType) return;

            string tmpName = col + "__tmp";
            var tmp = new DataColumn(tmpName, targetType);
            int ordinal = c.Ordinal;
            dt.Columns.Add(tmp);
            tmp.SetOrdinal(ordinal + 1);

            foreach (DataRow r in dt.Rows)
            {
                var v = r[c];
                if (v == DBNull.Value || v is null) { r[tmp] = DBNull.Value; continue; }

                try
                {
                    if (targetType == typeof(Guid))
                        r[tmp] = v is Guid g ? g : Guid.Parse(v.ToString()!);
                    else if (targetType == typeof(DateTime))
                        r[tmp] = v is DateTime d ? d : DateTime.Parse(v.ToString()!, CultureInfo.InvariantCulture);
                    else
                        r[tmp] = Convert.ChangeType(v, targetType, CultureInfo.InvariantCulture);
                }
                catch { r[tmp] = DBNull.Value; }
            }

            int ord = c.Ordinal;
            dt.Columns.Remove(c);
            tmp.ColumnName = col;
            tmp.SetOrdinal(ord);
        }

        private static void ToTime(DataTable dt, string col)
        {
            if (!dt.Columns.Contains(col)) return;

            foreach (DataRow r in dt.Rows)
            {
                var s = r[col]?.ToString();
                if (string.IsNullOrWhiteSpace(s)) { r[col] = DBNull.Value; continue; }

                // acepta "06:00 PM", "6:00 PM", "06:00", "6:00", "HH:mm:ss"
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtm))
                {
                    r[col] = dtm.ToString("HH:mm");
                    continue;
                }
                if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts))
                {
                    r[col] = $"{ts.Hours:00}:{ts.Minutes:00}";
                    continue;
                }

                var parts = s.Split(':');
                if (parts.Length >= 2 &&
                    int.TryParse(parts[0], out var h) &&
                    int.TryParse(parts[1], out var m))
                {
                    r[col] = $"{h:00}:{m:00}";
                }
                else
                {
                    r[col] = DBNull.Value;
                }
            }
        }

        private static (DataTable rowsOk, int rowsDropped, string sampleWhy)
            KeepRowsWithRequired(DataTable dt, params string[] requiredCols)
        {
            var ok = dt.Clone();
            int dropped = 0;
            string whySample = "";

            foreach (DataRow r in dt.Rows)
            {
                bool hasAll = true;
                foreach (var c in requiredCols)
                {
                    var s = dt.Columns.Contains(c) ? r[c]?.ToString() : null;
                    if (string.IsNullOrWhiteSpace(s))
                    {
                        hasAll = false;
                        if (string.IsNullOrEmpty(whySample)) whySample = $"Falta {c}";
                        break;
                    }
                }

                if (hasAll) ok.ImportRow(r); else dropped++;
            }
            return (ok, dropped, whySample);
        }

        // ---- CSV helpers (sencillos) ----
        private static DataTable CsvToDataTable(string path)
        {
            var dt = new DataTable();
            using var sr = new StreamReader(path, DetectEncoding(path));

            var headerLine = sr.ReadLine();
            if (headerLine == null) throw new InvalidOperationException("CSV sin encabezado.");
            var headers = SplitCsv(headerLine);
            foreach (var h in headers) dt.Columns.Add(h.Trim());

            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var parts = SplitCsv(line);
                var row = dt.NewRow();
                for (int i = 0; i < Math.Min(headers.Length, parts.Length); i++)
                    row[i] = parts[i];
                dt.Rows.Add(row);
            }
            return dt;
        }

        private static Encoding DetectEncoding(string path)
        {
            using var fs = File.OpenRead(path);
            if (fs.Length >= 3)
            {
                var bom = new byte[3];
                _ = fs.Read(bom, 0, 3);
                if (bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                    return new UTF8Encoding(true);
            }
            return new UTF8Encoding(false);
        }

        private static string[] SplitCsv(string input)
        {
            var list = new List<string>();
            bool inQuotes = false;
            var cur = new StringBuilder();

            for (int i = 0; i < input.Length; i++)
            {
                char ch = input[i];
                if (ch == '"')
                {
                    if (inQuotes && i + 1 < input.Length && input[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuotes = !inQuotes;
                }
                else if (ch == ',' && !inQuotes) { list.Add(cur.ToString()); cur.Clear(); }
                else { cur.Append(ch); }
            }
            list.Add(cur.ToString());
            return list.ToArray();
        }
    }
}
