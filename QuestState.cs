namespace SfsReader;

/// <summary>One quest stage in a quest record: the stage index and whether it's marked done.</summary>
public readonly record struct QuestStage(ushort Index, bool Done);

/// <summary>One quest objective in a quest record: index, state (1 shown, 2 completed, 3 completed and shown,
/// 4 failed, 5 failed and shown) and value.</summary>
public readonly record struct QuestObjective(uint Index, uint State, uint Value);

/// <summary>
/// The quest state held in the data of a save form type 8 (QUST) change record. Written from FORMAT.md,
/// "Quest state", only: one block per change-flag bit that is set, in the order 0, 1, 2, 31, 29, 28, 27, 26, 25, 24;
/// forms come from the record's reference list in order, and the first reference is the quest itself.
/// </summary>
public sealed class QuestState
{
    /// <summary>The change-flag bits that have a block, in the order the blocks are stored.</summary>
    public static readonly int[] BlockOrder = [0, 1, 2, 31, 29, 28, 27, 26, 25, 24];

    /// <summary>Base form data (change-flag bit 0): u32 form flags; null when the block is absent.</summary>
    public uint? FormFlags { get; private set; }
    /// <summary>Quest flags (change-flag bit 1); null when the block is absent.</summary>
    public uint? QuestFlags { get; private set; }
    /// <summary>Stages (change-flag bit 31); null when the block is absent.</summary>
    public IReadOnlyList<QuestStage>? Stages { get; private set; }
    /// <summary>Objectives (change-flag bit 29); null when the block is absent.</summary>
    public IReadOnlyList<QuestObjective>? Objectives { get; private set; }
    /// <summary>Forms named in the alias block (change-flag bit 28), in order; empty when the block is absent.</summary>
    public IReadOnlyList<FormRef> AliasForms { get; private set; } = [];
    /// <summary>Number of data bytes and references the decoder used (both equal the record's on success).</summary>
    public int BytesUsed { get; private set; }
    public int ReferencesUsed { get; private set; }

    /// <summary>Quest flag 0x1.</summary>
    public bool Enabled => ((QuestFlags ?? 0) & 0x1) != 0;
    /// <summary>Quest flag 0x2.</summary>
    public bool Completed => ((QuestFlags ?? 0) & 0x2) != 0;
    /// <summary>Quest flag 0x80: a start or stop is pending.</summary>
    public bool Pending => ((QuestFlags ?? 0) & 0x80) != 0;
    /// <summary>Quest flag 0x800: the tracked quest.</summary>
    public bool Active => ((QuestFlags ?? 0) & 0x800) != 0;

    /// <summary>starting (enabled + pending), running (enabled), stopping (pending only) or stopped (neither).
    /// "running" and "starting" also depend on fields the save doesn't hold.</summary>
    public string Status => (Enabled, Pending) switch
    {
        (true, true) => "starting",
        (true, false) => "running",
        (false, true) => "stopping",
        _ => "stopped",
    };

    /// <summary>The highest stage marked done (the game sets the current stage to it); null when none is done.</summary>
    public ushort? CurrentStage => Stages?.Where(s => s.Done).Select(s => (ushort?)s.Index).Max();

    /// <summary>Objective states 1, 3 and 5 are shown.</summary>
    public static bool ObjectiveShown(uint state) => state is 1 or 3 or 5;
    /// <summary>Objective states 2 and 3 are completed.</summary>
    public static bool ObjectiveCompleted(uint state) => state is 2 or 3;

    /// <summary>Decodes a quest record's data. Throws <see cref="InvalidDataException"/> when the blocks don't use up
    /// the data bytes and the reference list exactly.</summary>
    /// <param name="inner">The decompressed save data (the record's <see cref="ChangeRecord.DataOffset"/> points into it).</param>
    public static QuestState Decode(byte[] inner, ChangeRecord r)
    {
        if (r.FormType != 8)
            throw new InvalidDataException($"change record #{r.Index} has save form type {r.FormType}, not 8 (QUST)");
        if (r.DataOffset < 0 || r.DataOffset + r.DataSize > inner.LongLength)
            throw new InvalidDataException($"change record #{r.Index}: data at 0x{r.DataOffset:X} runs past the save data");
        var cur = new SaveBody.Cursor(inner, r.DataOffset, r.DataOffset + r.DataSize);
        var refs = new RefList(r.References);
        var q = new QuestState();
        try
        {
            refs.Next("the quest itself");
            uint cf = r.ChangeFlags;
            foreach (int bit in BlockOrder)
            {
                if ((cf & (1u << bit)) == 0) continue;
                switch (bit)
                {
                    case 0: q.FormFlags = cur.U32(); cur.U16(); break;
                    case 1: q.QuestFlags = cur.U32(); break;
                    case 2: cur.U32(); break;
                    case 31:
                    {
                        uint n = cur.Vsval();
                        cur.Need(3L * n, "stage list");
                        var list = new List<QuestStage>((int)n);
                        for (uint i = 0; i < n; i++) list.Add(new QuestStage(cur.U16(), cur.U8() != 0));
                        q.Stages = list;
                        break;
                    }
                    case 29:
                    {
                        uint n = cur.Vsval();
                        cur.Need(12L * n, "objective list");
                        var list = new List<QuestObjective>((int)n);
                        for (uint i = 0; i < n; i++) list.Add(new QuestObjective(cur.U32(), cur.U32(), cur.U32()));
                        q.Objectives = list;
                        break;
                    }
                    case 28: q.AliasForms = ReadAliases(ref cur, refs); break;
                    case 27:
                    {
                        cur.U32();
                        uint n = cur.Vsval();
                        for (uint i = 0; i < n; i++)
                        {
                            cur.U32();
                            uint a = cur.Vsval();
                            for (uint j = 0; j < a; j++) { cur.U32(); refs.Next("block 27"); refs.Next("block 27"); cur.U32(); }
                            uint b = cur.Vsval();
                            for (uint j = 0; j < b; j++) { refs.Next("block 27"); cur.U32(); }
                            cur.U16();
                            cur.U8();
                        }
                        break;
                    }
                    case 26: cur.U8(); break;
                    case 25: cur.U8(); cur.U32(); cur.U32(); break;
                    case 24: cur.Vsval(); break;
                }
            }
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"quest {r.Form} (change record #{r.Index}): {e.Message}", e);
        }
        q.BytesUsed = (int)(cur.Pos - r.DataOffset);
        q.ReferencesUsed = refs.Used;
        if (q.BytesUsed != r.DataSize || refs.Used != r.References.Count)
            throw new InvalidDataException($"quest {r.Form} (change record #{r.Index}): the blocks used {q.BytesUsed} of {r.DataSize} " +
                                           $"data bytes and {refs.Used} of {r.References.Count} references");
        return q;
    }

    static List<FormRef> ReadAliases(ref SaveBody.Cursor cur, RefList refs)
    {
        var forms = new List<FormRef>();
        cur.U8(); // has instance
        ushort n1 = cur.U16();
        for (int i = 0; i < n1; i++) forms.Add(refs.Next("alias block"));
        uint n2 = cur.U32();
        for (uint i = 0; i < n2; i++)
        {
            cur.U32(); // alias ID
            bool created = cur.U8() != 0;
            forms.Add(refs.Next("alias block"));
            if (created) for (int j = 0; j < 3; j++) forms.Add(refs.Next("alias block"));
        }
        uint n3 = cur.U32();
        for (uint i = 0; i < n3; i++) { cur.U32(); forms.Add(refs.Next("alias block")); }
        if (cur.U8() != 0)
        {
            cur.U32(); // story event type
            cur.U32(); // field
            uint values = cur.U32();
            for (uint i = 0; i < values; i++)
            {
                uint kind = cur.U32();
                switch (kind)
                {
                    case 0 or 1 or 2 or 4 or 5: forms.Add(refs.Next("story event")); break;
                    case 3: cur.U32(); break;
                    default: throw new InvalidDataException($"story event value kind {kind} isn't one of 0-5");
                }
            }
        }
        return forms;
    }

    sealed class RefList(IReadOnlyList<FormRef> refs)
    {
        public int Used { get; private set; }
        public FormRef Next(string what)
        {
            if (Used >= refs.Count)
                throw new InvalidDataException($"{what} needs reference {Used + 1}, but the record has {refs.Count}");
            return refs[Used++];
        }
    }
}
