using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace SphereNet.Persistence.Formats;

/// <summary>
/// Interpolated-string handler for save values: <c>w.WriteProperty("SERIAL", $"0{uid:x8}")</c>
/// formats into a pooled char buffer and hands the writer a span, so a writer that packs
/// bytes never materializes the string. Formatting is invariant-culture, which is what a
/// save must be regardless of the host's locale; for the integers, hex and GUIDs a record
/// holds that is the same text the culture-sensitive interpolation produced.
/// </summary>
[InterpolatedStringHandler]
public ref struct SaveValueBuilder
{
    private char[] _buffer;
    private int _length;

    public SaveValueBuilder(int literalLength, int formattedCount)
    {
        _buffer = ArrayPool<char>.Shared.Rent(Math.Max(64, literalLength + formattedCount * 16));
        _length = 0;
    }

    public readonly ReadOnlySpan<char> Text => _buffer.AsSpan(0, _length);

    public void AppendLiteral(string? value) => AppendSpan(value);

    public void AppendFormatted(string? value) => AppendSpan(value);

    public void AppendFormatted(ReadOnlySpan<char> value) => AppendSpan(value);

    // One overload per value type a record formats, so each is a direct TryFormat call.
    // Through the generic overload below every number was boxed - the JIT does not
    // remove the box there - and the boxes were a third of what a capture allocated.
    public void AppendFormatted(byte value, string? format = null) => AppendSpanFormattable(value, format);
    public void AppendFormatted(sbyte value, string? format = null) => AppendSpanFormattable(value, format);
    public void AppendFormatted(short value, string? format = null) => AppendSpanFormattable(value, format);
    public void AppendFormatted(ushort value, string? format = null) => AppendSpanFormattable(value, format);
    public void AppendFormatted(int value, string? format = null) => AppendSpanFormattable(value, format);
    public void AppendFormatted(uint value, string? format = null) => AppendSpanFormattable(value, format);
    public void AppendFormatted(long value, string? format = null) => AppendSpanFormattable(value, format);
    public void AppendFormatted(ulong value, string? format = null) => AppendSpanFormattable(value, format);
    public void AppendFormatted(Guid value, string? format = null) => AppendSpanFormattable(value, format);
    public void AppendFormatted(SphereNet.Core.Types.Point3D value, string? format = null) =>
        AppendSpanFormattable(value, format);

    private void AppendSpanFormattable<T>(T value, string? format) where T : ISpanFormattable
    {
        int written;
        while (!value.TryFormat(_buffer.AsSpan(_length), out written, format, CultureInfo.InvariantCulture))
            Grow(_buffer.Length * 2);
        _length += written;
    }

    public void AppendFormatted<T>(T value) => AppendFormatted(value, null);

    public void AppendFormatted<T>(T value, string? format)
    {
        if (value is ISpanFormattable)
        {
            int written;
            while (!((ISpanFormattable)value).TryFormat(_buffer.AsSpan(_length), out written,
                       format, CultureInfo.InvariantCulture))
                Grow(_buffer.Length * 2);
            _length += written;
            return;
        }
        AppendSpan(value is IFormattable formattable
            ? formattable.ToString(format, CultureInfo.InvariantCulture)
            : value?.ToString());
    }

    private void AppendSpan(ReadOnlySpan<char> value)
    {
        if (_length + value.Length > _buffer.Length)
            Grow(Math.Max(_buffer.Length * 2, _length + value.Length));
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    private void Grow(int size)
    {
        char[] next = ArrayPool<char>.Shared.Rent(size);
        _buffer.AsSpan(0, _length).CopyTo(next);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = next;
    }

    /// <summary>Hand the buffer back to the pool. The writer calls this once it has
    /// consumed <see cref="Text"/>.</summary>
    public void Dispose()
    {
        char[] buffer = _buffer;
        _buffer = [];
        _length = 0;
        if (buffer.Length > 0)
            ArrayPool<char>.Shared.Return(buffer);
    }
}
