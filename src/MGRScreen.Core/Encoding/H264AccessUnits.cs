namespace MGRScreen.Core.Encoding;

/// <summary>Splits the encoder's Annex B output at the AUD inserted into every access unit.</summary>
internal sealed class H264AccessUnits
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _length;
    private int _scan;
    private bool _seenAud;

    public void Append(ReadOnlySpan<byte> data, Action<byte[]> emit)
    {
        if (_length + data.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Frame H.264 excedeu o limite ou não contém delimitadores.");

        if (_buffer.Length < _length + data.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;

        var start = 0;
        for (; _scan + 3 < _length; _scan++)
        {
            if (_buffer[_scan] != 0 || _buffer[_scan + 1] != 0 ||
                _buffer[_scan + 2] != 1 || (_buffer[_scan + 3] & 31) != 9)
                continue;

            var boundary = _scan > 0 && _buffer[_scan - 1] == 0 ? _scan - 1 : _scan;
            if (_seenAud)
            {
                emit(_buffer.AsSpan(start, boundary - start).ToArray());
                start = boundary;
            }
            _seenAud = true;
        }

        if (start > 0)
        {
            _buffer.AsSpan(start, _length - start).CopyTo(_buffer);
            _length -= start;
            _scan -= start;
        }
    }
}
