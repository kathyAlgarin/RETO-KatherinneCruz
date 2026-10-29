using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace OperacionRescate.Api.Logging;

/// <summary>
/// JSON Lines: un objeto JSON por línea con las propiedades en el nivel superior (el formateador
/// por defecto las anida en "Properties") y el Timestamp en UTC ISO 8601 terminado en Z.
/// </summary>
public sealed class JsonLinesFormatter : ITextFormatter
{
    private static readonly JsonValueFormatter ValueFormatter = new(typeTagName: null);

    private static readonly HashSet<string> Reservadas =
        new(["Timestamp", "Level", "MessageTemplate", "RenderedMessage", "Exception"], StringComparer.Ordinal);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        output.Write("{\"Timestamp\":\"");
        output.Write(logEvent.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        output.Write("\",\"Level\":\"");
        output.Write(logEvent.Level.ToString());
        output.Write("\",\"MessageTemplate\":");
        JsonValueFormatter.WriteQuotedJsonString(logEvent.MessageTemplate.Text, output);
        output.Write(",\"RenderedMessage\":");
        JsonValueFormatter.WriteQuotedJsonString(logEvent.RenderMessage(CultureInfo.InvariantCulture), output);

        if (logEvent.Exception is not null)
        {
            // ToString() incluye tipo, mensaje y traza completa. Solo va al archivo, nunca a la respuesta HTTP.
            output.Write(",\"Exception\":");
            JsonValueFormatter.WriteQuotedJsonString(logEvent.Exception.ToString(), output);
        }

        foreach (var (nombre, valor) in logEvent.Properties)
        {
            output.Write(',');
            JsonValueFormatter.WriteQuotedJsonString(Reservadas.Contains(nombre) ? "Prop_" + nombre : nombre, output);
            output.Write(':');
            ValueFormatter.Format(valor, output);
        }

        output.Write("}\n");
    }
}
