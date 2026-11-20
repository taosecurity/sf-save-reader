namespace SfsReader.Tests;

/// <summary>Made-up quest-record fragments, one per block of FORMAT.md, "Quest state".</summary>
public class QuestTests
{
    static readonly FormRef Quest = new() { RefId = 0x400123, FormId = 0x123, Plugin = "Starfield.esm", LocalId = 0x123 };
    static FormRef Ref(uint id) => new() { RefId = 0x400000 | id, FormId = id, Plugin = "Starfield.esm", LocalId = id };

    static ChangeRecord Rec(uint changeFlags, byte[] data, params FormRef[] extraRefs) => new()
    {
        Index = 7, FormType = 8, Form = Quest, ChangeFlags = changeFlags, HasChangeFlags = true,
        References = [Quest, .. extraRefs], DataOffset = 0, DataSize = data.Length,
    };

    static QuestState Decode(uint changeFlags, Writer w, params FormRef[] extraRefs)
    {
        var d = w.ToArray();
        return QuestState.Decode(d, Rec(changeFlags, d, extraRefs));
    }

    [Fact]
    public void No_blocks_uses_only_the_quest_reference()
    {
        var q = Decode(0, new Writer());
        Assert.Null(q.QuestFlags);
        Assert.Null(q.Stages);
        Assert.Null(q.Objectives);
        Assert.Equal("stopped", q.Status);
        Assert.Null(q.CurrentStage);
        Assert.Equal(1, q.ReferencesUsed);
    }

    [Fact]
    public void Bit_0_is_form_flags_then_a_u16()
    {
        var q = Decode(0x1, new Writer().U32(0xABCD).U16(9));
        Assert.Equal(0xABCDu, q.FormFlags);
        Assert.Equal(6, q.BytesUsed);
    }

    [Theory]
    [InlineData(0x1u, "running", true, false, false)]
    [InlineData(0x81u, "starting", true, false, true)]
    [InlineData(0x80u, "stopping", false, false, true)]
    [InlineData(0x2u, "stopped", false, true, false)]
    [InlineData(0x803u, "running", true, true, false)]
    public void Bit_1_is_the_quest_flags(uint flags, string status, bool enabled, bool completed, bool pending)
    {
        var q = Decode(0x2, new Writer().U32(flags));
        Assert.Equal(flags, q.QuestFlags);
        Assert.Equal(status, q.Status);
        Assert.Equal(enabled, q.Enabled);
        Assert.Equal(completed, q.Completed);
        Assert.Equal(pending, q.Pending);
        Assert.Equal((flags & 0x800) != 0, q.Active);
    }

    [Fact]
    public void Bit_2_is_a_u32() => Assert.Equal(4, Decode(0x4, new Writer().U32(5)).BytesUsed);

    [Fact]
    public void Bit_31_is_the_stages_and_the_current_stage_is_the_highest_done()
    {
        var w = new Writer().Vsval(3).U16(10).U8(1).U16(200).U8(1).U16(9000).U8(0);
        var q = Decode(0x80000000, w);
        Assert.Equal([new QuestStage(10, true), new QuestStage(200, true), new QuestStage(9000, false)], q.Stages!);
        Assert.Equal((ushort)200, q.CurrentStage);
    }

    [Fact]
    public void Stage_0_done_is_stage_0_and_none_done_is_null()
    {
        Assert.Equal((ushort)0, Decode(0x80000000, new Writer().Vsval(1).U16(0).U8(2)).CurrentStage);
        Assert.Null(Decode(0x80000000, new Writer().Vsval(1).U16(5).U8(0)).CurrentStage);
        Assert.Empty(Decode(0x80000000, new Writer().Vsval(0)).Stages!);
    }

    [Fact]
    public void Bit_29_is_the_objectives()
    {
        var q = Decode(0x20000000, new Writer().Vsval(2).U32(10).U32(1).U32(0).U32(20).U32(3).U32(7));
        Assert.Equal([new QuestObjective(10, 1, 0), new QuestObjective(20, 3, 7)], q.Objectives!);
    }

    [Theory]
    [InlineData(0u, false, false)]
    [InlineData(1u, true, false)]
    [InlineData(2u, false, true)]
    [InlineData(3u, true, true)]
    [InlineData(4u, false, false)]
    [InlineData(5u, true, false)]
    public void Objective_states(uint state, bool shown, bool completed)
    {
        Assert.Equal(shown, QuestState.ObjectiveShown(state));
        Assert.Equal(completed, QuestState.ObjectiveCompleted(state));
    }

    [Fact]
    public void Bit_28_aliases_take_references_in_order()
    {
        var w = new Writer().U8(1)
            .U16(1)                                   // one reference
            .U32(2).U32(0).U8(0).U32(1).U8(1)         // alias 0 (1 ref), alias 1 created (4 refs)
            .U32(1).U32(5)                            // one (ID, reference)
            .U8(1).U32(9).U32(0).U32(2).U32(0).U32(3).U32(77); // story event: a reference value and a u32 value
        var refs = Enumerable.Range(1, 8).Select(i => Ref((uint)i)).ToArray();
        var q = Decode(0x10000000, w, refs);
        Assert.Equal(refs, q.AliasForms);
        Assert.Equal(9, q.ReferencesUsed);
    }

    [Fact]
    public void Bit_28_without_a_story_event()
    {
        var q = Decode(0x10000000, new Writer().U8(0).U16(0).U32(0).U32(0).U8(0));
        Assert.Empty(q.AliasForms);
    }

    [Fact]
    public void Bit_28_story_event_value_kind_6_is_refused() =>
        Assert.Throws<InvalidDataException>(() => Decode(0x10000000, new Writer().U8(0).U16(0).U32(0).U32(0).U8(1).U32(0).U32(0).U32(1).U32(6)));

    [Fact]
    public void Bit_27_entries()
    {
        var w = new Writer().U32(0).Vsval(1)
            .U32(0).Vsval(1).U32(0).U32(0)     // one (u32, ref, ref, u32)
            .Vsval(1).U32(0)                   // one (ref, u32)
            .U16(0).U8(0);
        // refs and u32s interleave: (u32, ref, ref, u32) takes 8 data bytes and 2 references
        var q = Decode(0x8000000, w, Ref(1), Ref(2), Ref(3));
        Assert.Equal(4, q.ReferencesUsed);
    }

    [Fact]
    public void Bits_26_25_24()
    {
        Assert.Equal(1, Decode(0x4000000, new Writer().U8(1)).BytesUsed);
        Assert.Equal(9, Decode(0x2000000, new Writer().U8(1).U32(2).U32(3)).BytesUsed);
        Assert.Equal(2, Decode(0x1000000, new Writer().Vsval(0x100)).BytesUsed);
    }

    [Fact]
    public void Blocks_come_in_the_order_0_1_2_31_29_28_27_26_25_24()
    {
        var w = new Writer().U32(1).U16(0)       // 0
            .U32(0x3)                             // 1
            .U32(0)                               // 2
            .Vsval(1).U16(100).U8(1)              // 31
            .Vsval(1).U32(5).U32(2).U32(0)        // 29
            .U8(0).U16(0).U32(0).U32(0).U8(0)     // 28
            .U32(0).Vsval(0)                      // 27
            .U8(0)                                // 26
            .U8(0).U32(0).U32(0)                  // 25
            .Vsval(0);                            // 24
        var q = Decode(0xBF000007, w);
        Assert.Equal("running", q.Status);
        Assert.True(q.Completed);
        Assert.Equal((ushort)100, q.CurrentStage);
        Assert.Single(q.Objectives!);
    }

    [Fact]
    public void Other_change_flag_bits_add_no_block() =>
        Assert.Equal("running", Decode(0x2 | 0x8 | 0x40000000, new Writer().U32(1)).Status);

    [Fact]
    public void Leftover_data_is_refused() =>
        Assert.Throws<InvalidDataException>(() => Decode(0x2, new Writer().U32(1).U8(0)));

    [Fact]
    public void Leftover_references_are_refused() =>
        Assert.Throws<InvalidDataException>(() => Decode(0x2, new Writer().U32(1), Ref(9)));

    [Fact]
    public void Missing_references_are_refused() =>
        Assert.Throws<InvalidDataException>(() => Decode(0x10000000, new Writer().U8(0).U16(1).U32(0).U32(0).U8(0)));

    [Fact]
    public void Data_too_short_is_refused() =>
        Assert.Throws<InvalidDataException>(() => Decode(0x80000000, new Writer().Vsval(2).U16(1).U8(1)));

    [Fact]
    public void Only_quest_records_are_decoded()
    {
        var d = new Writer().U32(1).ToArray();
        var r = new ChangeRecord { FormType = 0, Form = Quest, ChangeFlags = 2, References = [Quest], DataSize = 4 };
        Assert.Throws<InvalidDataException>(() => QuestState.Decode(d, r));
    }

    [Fact]
    public void Quest_from_a_whole_made_up_save()
    {
        var s = new FakeSave { Groups = [(8, 1)] };
        var data = new Writer().U32(0x1).Vsval(1).U16(10).U8(1).ToArray();
        s.Records.Add(new Writer().U8(0x02 | 0x08 | 0x20).Ref(1, 0x123).U32(0x80000002).Vsval(1).Vsval((uint)data.Length)
            .Ref(1, 0x123).Bytes(data).ToArray());
        var d = s.Build();
        var g = SaveGame.Parse(d);
        var body = SaveBody.Parse(d, g);
        var q = QuestState.Decode(d, body.Records[0]);
        Assert.Equal("running", q.Status);
        Assert.Equal((ushort)10, q.CurrentStage);
    }
}
