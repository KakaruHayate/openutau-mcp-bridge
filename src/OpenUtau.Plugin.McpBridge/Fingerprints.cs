using System.Security.Cryptography;
using System.Text;
using OpenUtau.Core.Ustx;

namespace OpenUtau.Plugin.McpBridge;

/// <summary>
/// Content fingerprints for stale-write protection. A read hands one out, a write takes
/// it back as <c>ifMatch</c>; the scope is exactly the fields the write replaces, so an
/// edit elsewhere in the project never invalidates it.
/// </summary>
internal static class Fingerprints
{
    public static string Note(UVoicePart part, UNote note)
    {
        var builder = new StringBuilder();
        builder.Append("note-content|").Append(part.Id).Append('|')
            .Append(note.position).Append('|')
            .Append(note.duration).Append('|')
            .Append(note.tone).Append('|')
            .Append(note.lyric).Append('|');
        AppendPitch(builder, note.pitch);
        builder.Append('|');
        AppendVibrato(builder, note.vibrato);
        return Hash(builder.ToString());
    }

    public static string Curve(UVoicePart part, string abbr)
    {
        var curve = part.curves.FirstOrDefault(c => c.abbr == abbr);
        var builder = new StringBuilder();
        builder.Append("curve|").Append(part.Id).Append('|').Append(abbr).Append('|');
        if (curve != null)
        {
            foreach (var x in curve.xs)
            {
                builder.Append(x).Append(',');
            }
            builder.Append('|');
            foreach (var y in curve.ys)
            {
                builder.Append(y).Append(',');
            }
        }
        return Hash(builder.ToString());
    }

    public static string Expression(UVoicePart part, UNote note, string abbr)
    {
        var builder = new StringBuilder();
        builder.Append("expression|").Append(part.Id).Append('|').Append(note.position).Append('|')
            .Append(note.duration).Append('|').Append(abbr).Append('|');
        foreach (var entry in note.phonemeExpressions.Where(e => e.abbr == abbr))
        {
            builder.Append(entry.index).Append('=').Append(entry.value.ToString("R")).Append(',');
        }
        return Hash(builder.ToString());
    }

    private static void AppendPitch(StringBuilder builder, UPitch pitch)
    {
        builder.Append(pitch.snapFirst ? '1' : '0');
        foreach (var point in pitch.data)
        {
            builder.Append('|').Append(point.X.ToString("R")).Append(',').Append(point.Y.ToString("R")).Append(',').Append(point.shape);
        }
    }

    private static void AppendVibrato(StringBuilder builder, UVibrato vibrato)
    {
        builder.Append(vibrato.length.ToString("R")).Append(',')
            .Append(vibrato.period.ToString("R")).Append(',')
            .Append(vibrato.depth.ToString("R")).Append(',')
            .Append(vibrato.@in.ToString("R")).Append(',')
            .Append(vibrato.@out.ToString("R")).Append(',')
            .Append(vibrato.shift.ToString("R")).Append(',')
            .Append(vibrato.drift.ToString("R")).Append(',')
            .Append(vibrato.volLink.ToString("R"));
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }
}
