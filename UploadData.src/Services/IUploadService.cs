// Services/IUploadService.cs
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using UploadData.Models; // usamos el modelo completo UploadResult

namespace UploadData.src.Services
{
    public interface IUploadService
    {
        /// <summary>
        /// Carga un CSV a STG.ofertas_raw.
        /// Si replacePeriod=true, borra previamente los registros de los per_codigo presentes en el archivo.
        /// </summary>
        Task<int> CargarCsvAsync(string csvPath, bool replacePeriod = false);

        /// <summary>
        /// Carga un DataTable ya preparado a STG.ofertas_raw.
        /// </summary>
        Task<int> CargarDataTableAsync(DataTable dt, bool replacePeriod = false);

        /// <summary>
        /// Lee un Excel y carga en STG.ofertas_raw.
        /// Devuelve información detallada de la carga (modelo UploadData.Models.UploadResult).
        /// </summary>
        Task<UploadResult> ProcessExcelToStagingAsync(
            string filePath, string? sheet, int? semestre, CancellationToken ct);

        /// <summary>
        /// Ejecuta el procedimiento post-carga (por ejemplo STG.p_ofertas_raw_postload)
        /// para normalizar/derivar columnas en SQL después del bulk insert.
        /// </summary>
        Task RunPostLoadAsync(CancellationToken ct);
    }
}
