// Controllers/UploadController.cs
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using UploadData.src.Services;

namespace UploadData.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UploadController : ControllerBase
{
    private readonly IUploadService _uploadService;
    private readonly long _maxFileSizeBytes;

    public UploadController(IUploadService uploadService, IConfiguration cfg)
    {
        _uploadService = uploadService;
        _maxFileSizeBytes = cfg.GetSection("Upload").GetValue<long>("MaxFileSizeBytes", 200_000_000);
    }

    // POST /api/Upload/excel/stg   -> carga a STG (staging/raw)
    [HttpPost("excel/stg")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadExcelToStaging([FromForm] UploadExcelDto dto, CancellationToken ct)
    {
        if (dto.File is null || dto.File.Length == 0)
            return BadRequest("Sube un archivo Excel válido.");

        if (dto.File.Length > _maxFileSizeBytes)
            return BadRequest($"El archivo excede el tamaño máximo permitido ({_maxFileSizeBytes:N0} bytes).");

        string tempPath = string.Empty;
        try
        {
            tempPath = await SaveTempAsync(dto.File, ct);

            // Procesa e inserta en STG.ofertas_raw
            var result = await _uploadService.ProcessExcelToStagingAsync(
                tempPath, dto.Sheet, dto.Semestre, ct);

            // Devolvemos el modelo completo (BatchId, Inserted, FileName, Worksheet, Warnings, Message)
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = "Error procesando el archivo.",
                detail = ex.Message
            });
        }
        finally
        {
            // Limpieza del archivo temporal
            if (!string.IsNullOrEmpty(tempPath) && System.IO.File.Exists(tempPath))
            {
                try { System.IO.File.Delete(tempPath); } catch { /* best effort */ }
            }
        }
    }

    // POST /api/Upload/excel/final -> (placeholder) carga a tabla final
    [HttpPost("excel/final")]
    [Consumes("multipart/form-data")]
    public IActionResult UploadExcelToFinal([FromForm] UploadExcelDto dto)
        => StatusCode(StatusCodes.Status501NotImplemented,
            new { message = "Pendiente: flujo a tabla FINAL aún no implementado." });

    // Helper para guardar archivo temporalmente
    private static async Task<string> SaveTempAsync(IFormFile file, CancellationToken ct)
    {
        var uploadsDir = Path.Combine(AppContext.BaseDirectory, "uploads");
        Directory.CreateDirectory(uploadsDir);
        var tempPath = Path.Combine(uploadsDir, $"{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        await using var fs = System.IO.File.Create(tempPath);
        await file.CopyToAsync(fs, ct);
        return tempPath;
    }
}

// DTO del request
public class UploadExcelDto
{
    [Required] public IFormFile File { get; set; } = default!;
    public string? Sheet { get; set; }

    // Queda por compatibilidad, el servicio hoy no lo usa para inferir per_codigo.
    public int? Semestre { get; set; }
}
