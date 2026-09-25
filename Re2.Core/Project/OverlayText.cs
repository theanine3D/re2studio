using System;
using System.Collections.Generic;
using Re2.Core.Assets;
using Re2.Core.Rom;

namespace Re2.Core.Project;

/// <summary>
/// Applies a project's overlay text -- the item names and their examine text, in each language the
/// build carries -- to a ROM being built.
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
        var alt = output.Layout.AlternateInventoryText;
        var languages = alt is null
            ? new[] { (false, (string?)null) }
            : new[] { (false, (string?)null), (true, alt.FileSuffix) };

        bool any = false;
        foreach (var (alternate, language) in languages)
            any |= ItemNameFile.ExistsIn(folder, language) || ItemTextFile.ExistsIn(folder, language);
        if (!any) return Result.Nothing;

        var overlay = ModelTextureTable.LoadMainOverlay(output);
        bool namesApplied = false, textApplied = false;

        foreach (var (alternate, language) in languages)
        {
            List<string>? names = null, messages = null;

            if (ItemNameFile.ExistsIn(folder, language) &&
                !ItemNameFile.TryRead(folder, out names, out string nameError, language))
                return new Result(false, false, nameError);

            if (ItemTextFile.ExistsIn(folder, language) &&
                !ItemTextFile.TryRead(folder, out messages, out string textError, language))
                return new Result(false, false, textError);

            bool namesDiffer = names is not null && !Same(ItemNames.Read(overlay, alternate), names);
            bool textDiffers = messages is not null && !Same(ItemMessages.Read(overlay, alternate), messages);

            if (namesDiffer && !ItemNames.TryWrite(overlay, names!, out string error, alternate))
                return new Result(false, false, error);

            if (textDiffers && !ItemMessages.TryWrite(overlay, messages!, out error, alternate))
                return new Result(false, false, error);

            namesApplied |= namesDiffer;
            textApplied |= textDiffers;
        }

        if (!namesApplied && !textApplied) return Result.Nothing;

        var main = OverlayTable.Read(output.Data)
                               .Find(e => e.Index == ModelTextureTable.MainOverlayIndex);

        if (main is null) return new Result(false, false, "The overlay table has no main overlay.");

        return OverlayWriter.TryReplace(output, main, overlay.Data, out string writeError)
            ? new Result(namesApplied, textApplied, "")
            : new Result(false, false, writeError);
    }

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
