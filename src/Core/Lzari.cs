namespace MemcardManager.Core;

/// <summary>
/// Haruhiko Okumura's LZARI (LZSS + adaptive arithmetic coding), as used by
/// Action Replay MAX (.max) save files. Port of the original public domain C code.
/// </summary>
internal sealed class Lzari
{
    const int N = 4096, F = 60, Threshold = 2, Nil = N;
    const int M = 15;
    const long Q1 = 1L << M, Q2 = 2 * Q1, Q3 = 3 * Q1, Q4 = 4 * Q1;
    const int MaxCum = (int)Q1 - 1;
    const int NChar = 256 - Threshold + F;

    readonly byte[] _text = new byte[N + F - 1];
    int _matchPos, _matchLen;
    readonly int[] _lson = new int[N + 1], _rson = new int[N + 257], _dad = new int[N + 1];

    readonly int[] _charToSym = new int[NChar], _symToChar = new int[NChar + 1];
    readonly int[] _symFreq = new int[NChar + 1], _symCum = new int[NChar + 1], _posCum = new int[N + 1];

    long _low, _high = Q4, _value;
    int _shifts;

    // bit I/O
    readonly List<byte> _out = new();
    int _putBuf, _putMask = 128;
    byte[] _in = Array.Empty<byte>();
    int _inPos, _getBuf, _getMask;

    void PutBit(int bit)
    {
        if (bit != 0) _putBuf |= _putMask;
        if ((_putMask >>= 1) == 0)
        {
            _out.Add((byte)_putBuf);
            _putBuf = 0;
            _putMask = 128;
        }
    }

    void FlushBits()
    {
        for (int i = 0; i < 7; i++) PutBit(0);
    }

    int GetBit()
    {
        if ((_getMask >>= 1) == 0)
        {
            _getBuf = _inPos < _in.Length ? _in[_inPos] : 0;
            _inPos++;
            _getMask = 128;
        }
        return (_getBuf & _getMask) != 0 ? 1 : 0;
    }

    void InitTree()
    {
        for (int i = N + 1; i <= N + 256; i++) _rson[i] = Nil;
        for (int i = 0; i < N; i++) _dad[i] = Nil;
    }

    void InsertNode(int r)
    {
        int i, p, cmp = 1, temp;
        p = N + 1 + _text[r];
        _rson[r] = _lson[r] = Nil;
        _matchLen = 0;
        for (; ; )
        {
            if (cmp >= 0)
            {
                if (_rson[p] != Nil) p = _rson[p];
                else { _rson[p] = r; _dad[r] = p; return; }
            }
            else
            {
                if (_lson[p] != Nil) p = _lson[p];
                else { _lson[p] = r; _dad[r] = p; return; }
            }
            for (i = 1; i < F; i++)
                if ((cmp = _text[r + i] - _text[p + i]) != 0) break;
            if (i > Threshold)
            {
                if (i > _matchLen)
                {
                    _matchPos = (r - p) & (N - 1);
                    if ((_matchLen = i) >= F) break;
                }
                else if (i == _matchLen)
                {
                    if ((temp = (r - p) & (N - 1)) < _matchPos) _matchPos = temp;
                }
            }
        }
        _dad[r] = _dad[p]; _lson[r] = _lson[p]; _rson[r] = _rson[p];
        _dad[_lson[p]] = r; _dad[_rson[p]] = r;
        if (_rson[_dad[p]] == p) _rson[_dad[p]] = r;
        else _lson[_dad[p]] = r;
        _dad[p] = Nil;
    }

    void DeleteNode(int p)
    {
        int q;
        if (_dad[p] == Nil) return;
        if (_rson[p] == Nil) q = _lson[p];
        else if (_lson[p] == Nil) q = _rson[p];
        else
        {
            q = _lson[p];
            if (_rson[q] != Nil)
            {
                do { q = _rson[q]; } while (_rson[q] != Nil);
                _rson[_dad[q]] = _lson[q]; _dad[_lson[q]] = _dad[q];
                _lson[q] = _lson[p]; _dad[_lson[p]] = q;
            }
            _rson[q] = _rson[p]; _dad[_rson[p]] = q;
        }
        _dad[q] = _dad[p];
        if (_rson[_dad[p]] == p) _rson[_dad[p]] = q;
        else _lson[_dad[p]] = q;
        _dad[p] = Nil;
    }

    void StartModel()
    {
        _symCum[NChar] = 0;
        for (int sym = NChar; sym >= 1; sym--)
        {
            int ch = sym - 1;
            _charToSym[ch] = sym;
            _symToChar[sym] = ch;
            _symFreq[sym] = 1;
            _symCum[sym - 1] = _symCum[sym] + _symFreq[sym];
        }
        _symFreq[0] = 0;
        _posCum[N] = 0;
        for (int i = N; i >= 1; i--) _posCum[i - 1] = _posCum[i] + 10000 / (i + 200);
    }

    void UpdateModel(int sym)
    {
        int i, c, chI, chSym;
        if (_symCum[0] >= MaxCum)
        {
            c = 0;
            for (i = NChar; i > 0; i--)
            {
                _symCum[i] = c;
                c += (_symFreq[i] = (_symFreq[i] + 1) >> 1);
            }
            _symCum[0] = c;
        }
        for (i = sym; _symFreq[i] == _symFreq[i - 1]; i--) { }
        if (i < sym)
        {
            chI = _symToChar[i]; chSym = _symToChar[sym];
            _symToChar[i] = chSym; _symToChar[sym] = chI;
            _charToSym[chI] = sym; _charToSym[chSym] = i;
        }
        _symFreq[i]++;
        while (--i >= 0) _symCum[i]++;
    }

    void Output(int bit)
    {
        PutBit(bit);
        for (; _shifts > 0; _shifts--) PutBit(bit == 0 ? 1 : 0);
    }

    void Renormalize()
    {
        for (; ; )
        {
            if (_high <= Q2) Output(0);
            else if (_low >= Q2) { Output(1); _low -= Q2; _high -= Q2; }
            else if (_low >= Q1 && _high <= Q3) { _shifts++; _low -= Q1; _high -= Q1; }
            else break;
            _low += _low; _high += _high;
        }
    }

    void EncodeChar(int ch)
    {
        int sym = _charToSym[ch];
        long range = _high - _low;
        _high = _low + range * _symCum[sym - 1] / _symCum[0];
        _low += range * _symCum[sym] / _symCum[0];
        Renormalize();
        UpdateModel(sym);
    }

    void EncodePosition(int position)
    {
        long range = _high - _low;
        _high = _low + range * _posCum[position] / _posCum[0];
        _low += range * _posCum[position + 1] / _posCum[0];
        Renormalize();
    }

    void EncodeEnd()
    {
        _shifts++;
        Output(_low < Q1 ? 0 : 1);
        FlushBits();
    }

    int BinarySearchSym(long x)
    {
        int i = 1, j = NChar;
        while (i < j)
        {
            int k = (i + j) / 2;
            if (_symCum[k] > x) i = k + 1; else j = k;
        }
        return i;
    }

    int BinarySearchPos(long x)
    {
        int i = 1, j = N;
        while (i < j)
        {
            int k = (i + j) / 2;
            if (_posCum[k] > x) i = k + 1; else j = k;
        }
        return i - 1;
    }

    void DecodeRenormalize()
    {
        for (; ; )
        {
            if (_low >= Q2) { _value -= Q2; _low -= Q2; _high -= Q2; }
            else if (_low >= Q1 && _high <= Q3) { _value -= Q1; _low -= Q1; _high -= Q1; }
            else if (_high > Q2) break;
            _low += _low; _high += _high;
            _value = 2 * _value + GetBit();
        }
    }

    int DecodeChar()
    {
        long range = _high - _low;
        int sym = BinarySearchSym(((_value - _low + 1) * _symCum[0] - 1) / range);
        _high = _low + range * _symCum[sym - 1] / _symCum[0];
        _low += range * _symCum[sym] / _symCum[0];
        DecodeRenormalize();
        int ch = _symToChar[sym];
        UpdateModel(sym);
        return ch;
    }

    int DecodePosition()
    {
        long range = _high - _low;
        int position = BinarySearchPos(((_value - _low + 1) * _posCum[0] - 1) / range);
        _high = _low + range * _posCum[position] / _posCum[0];
        _low += range * _posCum[position + 1] / _posCum[0];
        DecodeRenormalize();
        return position;
    }

    public static byte[] Encode(ReadOnlySpan<byte> src)
    {
        var z = new Lzari();
        z.EncodeImpl(src);
        return z._out.ToArray();
    }

    void EncodeImpl(ReadOnlySpan<byte> src)
    {
        int inPos = 0;
        int i, c, len, r, s, lastMatchLen;
        StartModel();
        InitTree();
        s = 0;
        r = N - F;
        for (i = s; i < r; i++) _text[i] = (byte)' ';
        for (len = 0; len < F && inPos < src.Length; len++) _text[r + len] = src[inPos++];
        if (len == 0) return;
        for (i = 1; i <= F; i++) InsertNode(r - i);
        InsertNode(r);
        do
        {
            if (_matchLen > len) _matchLen = len;
            if (_matchLen <= Threshold)
            {
                _matchLen = 1;
                EncodeChar(_text[r]);
            }
            else
            {
                EncodeChar(255 - Threshold + _matchLen);
                EncodePosition(_matchPos - 1);
            }
            lastMatchLen = _matchLen;
            for (i = 0; i < lastMatchLen && inPos < src.Length; i++)
            {
                c = src[inPos++];
                DeleteNode(s);
                _text[s] = (byte)c;
                if (s < F - 1) _text[s + N] = (byte)c;
                s = (s + 1) & (N - 1);
                r = (r + 1) & (N - 1);
                InsertNode(r);
            }
            while (i++ < lastMatchLen)
            {
                DeleteNode(s);
                s = (s + 1) & (N - 1);
                r = (r + 1) & (N - 1);
                if (--len != 0) InsertNode(r);
            }
        } while (len > 0);
        EncodeEnd();
    }

    public static byte[] Decode(byte[] src, int outLength)
    {
        var z = new Lzari { _in = src };
        return z.DecodeImpl(outLength);
    }

    byte[] DecodeImpl(int outLength)
    {
        var output = new byte[outLength];
        if (outLength == 0) return output;
        for (int i = 0; i < M + 2; i++) _value = 2 * _value + GetBit();
        StartModel();
        for (int i = 0; i < N - F; i++) _text[i] = (byte)' ';
        int r = N - F;
        int count = 0;
        while (count < outLength)
        {
            if (_inPos > _in.Length + 16) throw new InvalidDataException("MAX-Daten sind beschädigt (LZARI).");
            int c = DecodeChar();
            if (c < 256)
            {
                output[count++] = (byte)c;
                _text[r++] = (byte)c;
                r &= N - 1;
            }
            else
            {
                int i = (r - DecodePosition() - 1) & (N - 1);
                int j = c - 255 + Threshold;
                for (int k = 0; k < j && count < outLength; k++)
                {
                    byte b = _text[(i + k) & (N - 1)];
                    output[count++] = b;
                    _text[r++] = b;
                    r &= N - 1;
                }
            }
        }
        return output;
    }
}
