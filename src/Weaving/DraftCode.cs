using System;
using System.Collections.Generic;
using System.Linq;

namespace ArtsAndCrafts.Tailor.Weaving;

/// <summary>
/// A draft as the owner's draft designer exports it (docs/weaving-draft-designer.html, "For modders"): size N (16 / 32),
/// shafts S (2..4), treadles T (4 / 6); per warp end its colour and shaft; per treadle its lift mask (bit s = shaft s+1);
/// per row its treadle and weft colour. Colours are designer palette indices (<see cref="DraftCode.PaletteCodes"/>).
/// </summary>
public sealed record DraftCodeData(int N, int S, int T, int[] Warp, int[] Threading, int[] TieUp, int[] Treadling, int[] Weft);

/// <summary>
/// The designer's export code (lane/warp-shuttles-import): <c>ACD1:</c> + base64url (no padding) of the bytes
/// [version 1, N, S, T, nibble stream (high nibble first: N warp colours, N threading, T tie-up masks, N treadling,
/// N weft colours; padded with a 0 nibble), checksum = sum of all previous bytes mod 256]. Same validation as the
/// designer's <c>decode</c>; errors are lang keys (<c>actailor:draft-err-*</c>) plus arguments. Pure (selftest).
/// </summary>
public static class DraftCode
{
    public const string Prefix = "ACD1:";
    public const int Version = 1;

    /// <summary>
    /// Designer palette index → twine code: flax, white, black, gray, red, blue, yellow, green, purple, pink, orange,
    /// brown, mordant, gold (the designer's PAL order).
    /// </summary>
    public static readonly string[] PaletteCodes =
    {
        "game:flaxtwine", "actailor:twine-white", "actailor:twine-black", "actailor:twine-gray", "actailor:twine-red",
        "actailor:twine-blue", "actailor:twine-yellow", "actailor:twine-green", "actailor:twine-purple", "actailor:twine-pink",
        "actailor:twine-orange", "actailor:twine-brown", "actailor:twine-mordant", "actailor:twine-gold",
    };

    public readonly record struct Error(string Key, object[] Args)
    {
        public static Error Of(string key, params object[] args) => new(key, args);
    }

    /// <summary>Decode <paramref name="code"/> (whitespace ignored, prefix case-insensitive); null + <paramref name="error"/> when invalid.</summary>
    public static DraftCodeData? Decode(string? code, out Error? error)
    {
        error = null;
        code = new string((code ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (code.Length < Prefix.Length || !code.Substring(0, Prefix.Length).Equals(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = Error.Of("draft-err-prefix");
            return null;
        }
        byte[]? b = FromBase64Url(code.Substring(Prefix.Length));
        if (b == null) { error = Error.Of("draft-err-chars"); return null; }
        if (b.Length < 5) { error = Error.Of("draft-err-short"); return null; }
        int ver = b[0], n = b[1], s = b[2], t = b[3];
        if (ver != Version) { error = Error.Of("draft-err-version", ver); return null; }
        if ((n != 16 && n != 32) || s < 2 || s > 4 || (t != 4 && t != 6)) { error = Error.Of("draft-err-counts", n, s, t); return null; }
        int nibbles = 4 * n + t;
        if (b.Length != 4 + (nibbles + 1) / 2 + 1) { error = Error.Of("draft-err-length"); return null; }
        int sum = 0;
        for (int i = 0; i < b.Length - 1; i++) sum = (sum + b[i]) & 255;
        if (sum != b[^1]) { error = Error.Of("draft-err-checksum"); return null; }
        var nib = new List<int>();
        for (int i = 4; i < b.Length - 1; i++) { nib.Add(b[i] >> 4); nib.Add(b[i] & 15); }
        int p = 0;
        int[] Take(int count) { var r = nib.Skip(p).Take(count).ToArray(); p += count; return r; }
        var d = new DraftCodeData(n, s, t, Take(n), Take(n), Take(t), Take(n), Take(n));
        if (d.Warp.Any(c => c >= PaletteCodes.Length) || d.Weft.Any(c => c >= PaletteCodes.Length) || d.Threading.Any(v => v >= s)
            || d.TieUp.Any(m => m >= 1 << s) || d.Treadling.Any(v => v >= t))
        {
            error = Error.Of("draft-err-range");
            return null;
        }
        return d;
    }

    /// <summary>The designer's <c>encode</c> (for tests and tools).</summary>
    public static string Encode(DraftCodeData d)
    {
        var nib = d.Warp.Concat(d.Threading).Concat(d.TieUp).Concat(d.Treadling).Concat(d.Weft).ToList();
        if (nib.Count % 2 != 0) nib.Add(0);
        var b = new List<byte> { Version, (byte)d.N, (byte)d.S, (byte)d.T };
        for (int i = 0; i < nib.Count; i += 2) b.Add((byte)((nib[i] << 4) | (nib[i + 1] & 15)));
        int sum = 0;
        foreach (byte x in b) sum = (sum + x) & 255;
        b.Add((byte)sum);
        return Prefix + Convert.ToBase64String(b.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>base64url (RFC 4648 §5) without padding; also accepts '+', '/' and padding. Null on a bad character.</summary>
    static byte[]? FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        if (s.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '+' || c == '/'))) return null;
        if (s.Length % 4 == 1) return null;
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}
