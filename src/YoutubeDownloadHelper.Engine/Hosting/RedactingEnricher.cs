using Serilog.Core;
using Serilog.Events;

namespace YoutubeDownloadHelper.Engine.Hosting;

/// <summary>
/// Applies <see cref="LogRedactor"/> to every property of every log event before any sink sees it, so the rendered
/// message is redacted too. A property with a sensitive name (e.g. <c>Authorization</c>, <c>Cookies</c>,
/// <c>Token</c>) is masked whole. Message templates are constant text and are not rewritten.
/// </summary>
public sealed class RedactingEnricher(LogRedactor redactor) : ILogEventEnricher
{
    private static readonly ScalarValue Masked = new(LogRedactor.Mask);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var redacted = LogRedactor.IsSensitiveName(name) ? Masked : Redact(value);
            if (!ReferenceEquals(redacted, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
            }
        }
    }

    /// <summary>The redacted value, or the same instance when nothing changed.</summary>
    private LogEventPropertyValue Redact(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: string text }:
                var redacted = redactor.Redact(text);
                return string.Equals(redacted, text, StringComparison.Ordinal) ? value : new ScalarValue(redacted);

            case SequenceValue sequence:
                var elements = sequence.Elements.Select(Redact).ToList();
                return elements.SequenceEqual(sequence.Elements, ReferenceEqualityComparer.Instance)
                    ? value
                    : new SequenceValue(elements);

            case StructureValue structure:
                var properties = structure.Properties
                    .Select(p => LogRedactor.IsSensitiveName(p.Name) ? new LogEventProperty(p.Name, Masked) : RedactProperty(p))
                    .ToList();
                return properties.SequenceEqual(structure.Properties, ReferenceEqualityComparer.Instance)
                    ? value
                    : new StructureValue(properties, structure.TypeTag);

            case DictionaryValue dictionary:
                var changed = false;
                var entries = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                foreach (var (key, element) in dictionary.Elements)
                {
                    var newValue = key.Value is string k && LogRedactor.IsSensitiveName(k) ? Masked : Redact(element);
                    changed |= !ReferenceEquals(newValue, element);
                    entries.Add(new(key, newValue));
                }

                return changed ? new DictionaryValue(entries) : value;

            default:
                return value;
        }
    }

    private LogEventProperty RedactProperty(LogEventProperty property)
    {
        var redacted = Redact(property.Value);
        return ReferenceEquals(redacted, property.Value) ? property : new LogEventProperty(property.Name, redacted);
    }
}
