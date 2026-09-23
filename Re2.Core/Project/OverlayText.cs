using System;
using System.Collections.Generic;
using Re2.Core.Assets;
using Re2.Core.Rom;

namespace Re2.Core.Project;

/// <summary>
/// Applies a project's overlay text -- the item names and their examine text -- to a ROM being built.
/// </summary>
public static class OverlayText
{
    public sealed record Result(bool NamesApplied, bool MessagesApplied, string Error)
    {
        public bool Applied => NamesApplied || MessagesApplied;

        public static readonly Result Nothing = new(false, false, "");
    }

    public static Result Apply(string folder, RomFile output)
    {
        bool haveNames = ItemNameFile.ExistsIn(folder);
        bool haveText = ItemTextFile.ExistsIn(folder);
        if (!haveNames && !haveText) return Result.Nothing;

        List<string>? names = null, messages = null;

        if (haveNames && !ItemNameFile.TryRead(folder, out names, out string nameError))
            return new Result(false, false, nameError);

        if (haveText && !ItemTextFile.TryRead(folder, out messages, out string textError))
            return new Result(false, false, textError);

        var overlay = ModelTextureTable.LoadMainOverlay(output);

        bool namesDiffer = names is not null && !Same(ItemNames.Read(overlay), names);
        bool textDiffers = messages is not null && !Same(ItemMessages.Read(overlay), messages);

        if (!namesDiffer && !textDiffers) return Result.Nothing;

        if (namesDiffer && !ItemNames.TryWrite(overlay, names!, out string error))
            return new Result(false, false, error);

        if (textDiffers && !ItemMessages.TryWrite(overlay, messages!, out error))
            return new Result(false, false, error);

        var main = OverlayTable.Read(output.Data)
                               .Find(e => e.Index == ModelTextureTable.MainOverlayIndex);

        if (main is null) return new Result(false, false, "The overlay table has no main overlay.");

        return OverlayWriter.TryReplace(output, main, overlay.Data, out error)
            ? new Result(namesDiffer, textDiffers, "")
            : new Result(false, false, error);
    }

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
