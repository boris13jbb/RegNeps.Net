namespace RegNeps.Application.Records;

public sealed class CreateNepRecordRequest
{
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;

    /// <summary>
    /// Identidad estable de la operación de creación (anti-duplicado por reintento / doble clic).
    /// Ligada al usuario en el servidor; no sustituye la clave primaria del registro.
    /// </summary>
    public string? ClientOperationId { get; set; }

    /// <summary>
    /// Sesión de captura activa (varias mediciones). Distinto de ClientOperationId.
    /// </summary>
    public string? CaptureSessionId { get; set; }

    // Campos de propiedad: ignorados en Create. El propietario sale de RecordActor.
    [Obsolete("No enviar desde el cliente; el propietario se toma de la identidad autenticada.")]
    public string? CreatedByUserId { get; set; }

    [Obsolete("No enviar desde el cliente; el propietario se toma de la identidad autenticada.")]
    public string? CreatedByEmail { get; set; }

    [Obsolete("No enviar desde el cliente; el propietario se toma de la identidad autenticada.")]
    public string? CreatedByRole { get; set; }
}

public sealed class UpdateNepRecordRequest
{
    public Guid Id { get; set; }
    public string Telar { get; set; } = string.Empty;
    public double Neps { get; set; }
    public string Tela { get; set; } = string.Empty;
    public string LoteTrama { get; set; } = string.Empty;
    public string Turno { get; set; } = string.Empty;
    public string Operario { get; set; } = string.Empty;
    public string LineaProduccion { get; set; } = string.Empty;
    public string Observacion { get; set; } = string.Empty;

    /// <summary>Stamp leído al abrir la edición; si no coincide, hay conflicto.</summary>
    public string? ExpectedConcurrencyStamp { get; set; }
}
