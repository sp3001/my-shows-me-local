using Shows.Infrastructure.Persistence;
using Xunit;

namespace Shows.Tests.Unit;

/// <summary>
/// Step 1.6 acceptance for <see cref="IdGenerator"/>. The encoding is owned by SCHEMA.md §1a; these
/// tests pin the properties that are invisible when wrong — monotonicity across a clock rollback, the
/// exact bit layout, and that two processes (salts) never collide.
/// </summary>
public sealed class IdGeneratorTests
{
    [Fact]
    public void One_hundred_thousand_ids_are_strictly_increasing()
    {
        // The load-bearing property: episode_watch.episode_id points at these, so a duplicate or a
        // backwards id silently orphans watch marks while every row count still looks right.
        var generator = new IdGenerator();
        var previous = long.MinValue;

        for (var i = 0; i < 100_000; i++)
        {
            var id = generator.NextId();
            Assert.True(id > previous, $"id {i} ({id}) is not greater than the previous ({previous}).");
            Assert.True(id > 0, $"id {i} ({id}) is not positive.");
            previous = id;
        }
    }

    [Fact]
    public void Id_decodes_to_the_clock_instant_it_was_minted_at()
    {
        // Bit layout check: (ms - epoch) << 22 | salt << 12 | sequence. Decoding the high field must
        // return the mint instant, proving the shifts and the epoch are applied as SCHEMA.md §1a says.
        const long fixedMs = 1_767_225_600_000; // 2026-01-01T00:00:00Z + some offset, fixed for the test
        var generator = new IdGenerator(() => fixedMs, salt: 7);

        var id = generator.NextId();

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(fixedMs), IdGenerator.DecodeTimestamp(id));
    }

    [Fact]
    public void Real_clock_id_decodes_to_within_a_few_seconds_of_now()
    {
        // The plan's shift-width catcher: with the real clock, (id >> 22) + epoch must land within a few
        // seconds of UtcNow. A wrong shift or epoch passes every other test and fails here.
        var generator = new IdGenerator();
        var before = DateTimeOffset.UtcNow;
        var id = generator.NextId();
        var after = DateTimeOffset.UtcNow;

        var decoded = IdGenerator.DecodeTimestamp(id);
        Assert.InRange(decoded, before.AddSeconds(-5), after.AddSeconds(5));
    }

    [Fact]
    public void Bit_layout_sums_to_exactly_sixty_four_bits()
    {
        // ADR-027: there is no spare bit. A 65-bit layout overflows long into negative ids that sort
        // before everything and read like corruption; a 63-bit layout wastes a bit of timestamp range.
        const int total = IdGenerator.SignBits + IdGenerator.TimestampBits
            + IdGenerator.SaltBits + IdGenerator.SequenceBits;
        Assert.Equal(64, total);
    }

    [Fact]
    public void Salt_and_sequence_occupy_their_declared_bit_fields()
    {
        // Decode the low 22 bits: salt in bits [12..21], sequence in [0..11]. With a fixed clock the
        // sequence advances, so the low field must equal (salt << 12) | sequence exactly.
        const long fixedMs = 1_767_225_600_000;
        var generator = new IdGenerator(() => fixedMs, salt: 0b10_1010_1010); // 682

        var first = generator.NextId();
        var second = generator.NextId();

        Assert.Equal((682L << 12) | 0, first & 0x3F_FFFF);
        Assert.Equal((682L << 12) | 1, second & 0x3F_FFFF);
    }

    [Fact]
    public void Two_salts_never_collide_over_the_same_sequence()
    {
        // Two processes minting at the same millisecond and the same sequence must still differ — the
        // 10-bit salt is what keeps a second process's ids distinct (SCHEMA.md §1a).
        const long fixedMs = 1_767_225_600_000;
        var a = new IdGenerator(() => fixedMs, salt: 3);
        var b = new IdGenerator(() => fixedMs, salt: 4);

        Assert.NotEqual(a.NextId(), b.NextId());
    }

    [Fact]
    public void Two_salts_minting_in_a_tight_loop_share_no_id()
    {
        // The probabilistic check the plan names: two processes (salts) minting hard at the same wall
        // clock must never hand out the same id. With a fixed clock the sequences run in lockstep, so
        // every pair differs only by the salt field — the strongest version of the collision test.
        // Salt values run: 137 and 901.
        const long fixedMs = 1_767_225_600_000;
        var a = new IdGenerator(() => fixedMs, salt: 137);
        var b = new IdGenerator(() => fixedMs, salt: 901);

        var seen = new HashSet<long>();
        for (var i = 0; i < 4_096; i++)
        {
            Assert.True(seen.Add(a.NextId()));
            Assert.True(seen.Add(b.NextId()));
        }

        Assert.Equal(8_192, seen.Count); // zero collisions across both streams
    }

    [Fact]
    public void Small_clock_rollback_keeps_minting_at_the_last_timestamp()
    {
        // Rollback ≤ 2 s: the generator must NOT mint backwards. It holds the timestamp at the previous
        // value and advances the sequence, so the next id is still strictly greater.
        long now = 1_767_225_600_000;
        var generator = new IdGenerator(() => now, salt: 1);

        var before = generator.NextId();
        now -= 1_000; // a 1-second jump backwards, within tolerance
        var after = generator.NextId();

        Assert.True(after > before);
    }

    [Fact]
    public void Large_clock_rollback_throws_instead_of_minting_a_future_id()
    {
        // Rollback > 2 s: minting would claim a timestamp the clock now says is in the past relative to
        // ids already handed out. SCHEMA.md §1a says throw, not silently mint backwards.
        long now = 1_767_225_600_000;
        var generator = new IdGenerator(() => now, salt: 1);

        generator.NextId();
        now -= 60_000; // a 60-second jump backwards, beyond tolerance

        Assert.Throws<InvalidOperationException>(() => generator.NextId());
    }

    [Fact]
    public void Sequence_exhaustion_spins_to_the_next_millisecond()
    {
        // 4096 ids fit in one millisecond (12-bit sequence). The 4097th must move the timestamp forward
        // by one rather than wrap the sequence to zero and duplicate an earlier id.
        const long fixedMs = 1_767_225_600_000;
        var generator = new IdGenerator(() => fixedMs, salt: 5);

        long previous = long.MinValue;
        for (var i = 0; i < 4096; i++)
        {
            var id = generator.NextId();
            Assert.True(id > previous);
            previous = id;
        }

        // The next id after exhaustion must still be strictly greater and must carry a later timestamp.
        var overflow = generator.NextId();
        Assert.True(overflow > previous);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(fixedMs).AddMilliseconds(1),
            IdGenerator.DecodeTimestamp(overflow));
    }
}
