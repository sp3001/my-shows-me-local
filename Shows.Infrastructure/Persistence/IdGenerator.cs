using System.Security.Cryptography;
using Shows.Application;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// Mints the client-generated surrogate keys (SPEC R13, ADR-026/ADR-027). The encoding is owned by
/// <c>SCHEMA.md</c> §1a; this is its only implementation.
/// </summary>
/// <remarks>
/// Layout — a positive 64-bit integer summing to exactly 64 with <b>no spare bit</b>:
/// <code>1 sign | 41 ms-since-epoch | 10 process-salt | 12 sequence</code>.
/// Mint: <c>(ms - epoch) &lt;&lt; 22 | salt &lt;&lt; 12 | sequence</c>. The mint is <c>lock</c>ed — the app
/// mints from UI threads and the HTTP callback path alike, so it is not thread-safe by accident.
/// </remarks>
public sealed class IdGenerator : IIdGenerator
{
    // The epoch is a named constant with a comment, never a literal buried in a shift — a wrong epoch
    // is invisible until 2095 (SCHEMA.md §1a). ~69-year horizon from here.
    private static readonly long EpochUnixMs =
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // Bit widths. Check the sum before changing any width: 1 + 41 + 10 + 12 = 64. A 65-bit layout
    // silently overflows long into negative ids, which sort before everything and read like corruption.
    internal const int SignBits = 1;
    internal const int TimestampBits = 41;
    internal const int SaltBits = 10;
    internal const int SequenceBits = 12;

    internal const int SequenceShift = 0;
    internal const int SaltShift = SequenceBits;                 // 12
    internal const int TimestampShift = SequenceBits + SaltBits; // 22

    private const long SequenceMask = (1L << SequenceBits) - 1;  // 4095
    private const long SaltMask = (1L << SaltBits) - 1;          // 1023

    // A clock rollback of up to this many milliseconds is absorbed by minting at the last timestamp;
    // beyond it we throw rather than mint ids claiming a time the clock says is future (SCHEMA.md §1a).
    private const long RollbackToleranceMs = 2_000;

    private readonly Func<long> _clock;
    private readonly long _salt;
    private readonly object _gate = new();

    private long _lastTimestamp = -1;
    private long _sequence;

    /// <summary>Process-launch constructor: real clock, salt drawn once from a CSPRNG.</summary>
    public IdGenerator()
        : this(static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
               RandomNumberGenerator.GetInt32(0, (int)SaltMask + 1))
    {
    }

    // Test seam: a controllable clock and an explicit salt. Production uses the parameterless ctor.
    internal IdGenerator(Func<long> clock, long salt)
    {
        _clock = clock;
        _salt = salt & SaltMask;
    }

    public long NextId()
    {
        lock (_gate)
        {
            var elapsed = _clock() - EpochUnixMs;

            if (elapsed < _lastTimestamp)
            {
                if (_lastTimestamp - elapsed > RollbackToleranceMs)
                {
                    throw new InvalidOperationException(
                        $"Clock rolled back {_lastTimestamp - elapsed} ms beyond the "
                        + $"{RollbackToleranceMs} ms tolerance; refusing to mint ids claiming a future time "
                        + "(SCHEMA.md §1a).");
                }

                // Small rollback: keep minting at the last timestamp and let the sequence advance.
                elapsed = _lastTimestamp;
            }

            if (elapsed == _lastTimestamp)
            {
                _sequence++;
                if (_sequence > SequenceMask)
                {
                    // Sequence exhausted in this millisecond: spin to the next one. Never wrap — a wrap
                    // would duplicate an id minted earlier in the same ms.
                    elapsed = _lastTimestamp + 1;
                    _sequence = 0;
                }
            }
            else
            {
                _sequence = 0;
            }

            _lastTimestamp = elapsed;

            return (elapsed << TimestampShift) | (_salt << SaltShift) | _sequence;
        }
    }

    /// <summary>Decodes the millisecond field back to a UTC instant (SCHEMA.md §1a's decode check).</summary>
    internal static DateTimeOffset DecodeTimestamp(long id) =>
        DateTimeOffset.FromUnixTimeMilliseconds((id >> TimestampShift) + EpochUnixMs);

    internal static long EpochUnixMsValue => EpochUnixMs;
}
