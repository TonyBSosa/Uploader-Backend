// File: Models/UploadResult.cs
using System;
using System.Collections.Generic;

namespace UploadData.Models
{
    /// <summary>
    /// Resultado de una carga de archivo Excel a STG.ofertas_raw.
    /// </summary>
    public sealed class UploadResult
    {
        /// <summary>Identificador único de la carga (mismo para todas las filas del archivo).</summary>
        public Guid BatchId { get; set; }

        /// <summary>Cantidad de filas insertadas en STG.ofertas_raw.</summary>
        public int Inserted { get; set; }

        /// <summary>Nombre del archivo fuente subido.</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>Nombre de la hoja de Excel utilizada.</summary>
        public string? Worksheet { get; set; }

        /// <summary>Mensajes resumidos (advertencias no fatales).</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>Mensaje informativo general.</summary>
        public string? Message { get; set; }
    }
}
