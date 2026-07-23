#!/usr/bin/env bash
# Verify every game member DeckView hooks by *string* (Harmony patch targets + AccessTools
# reflection) actually exists in the game's sts2.dll. The C# compiler already checks members
# accessed through typed APIs; it CANNOT check these string-named ones, so this is where a
# game update could move them. DeckView now runs the same inventory as an in-game preflight and
# safely disables itself when a member is absent. Run this after each game update for a readable
# maintainer report before launching.
#
#   scripts/verify-hooks.sh
#
# Requires ilspycmd (see DEVELOPMENT.md). Exits non-zero if any member is missing.
set -u

if [ -n "${ILSPY:-}" ]; then
    ILSPY="$ILSPY"
elif command -v ilspycmd >/dev/null 2>&1; then
    ILSPY="$(command -v ilspycmd)"
else
    ILSPY="/mnt/c/Users/ernes/.dotnet/tools/ilspycmd.exe"
fi
DLL="${STS2_DLL:-C:\\Program Files (x86)\\Steam\\steamapps\\common\\Slay the Spire 2\\data_sts2_windows_x86_64\\sts2.dll}"

if [ ! -x "$ILSPY" ]; then
    printf 'ilspycmd not found at %s; set ILSPY to its executable path\n' "$ILSPY" >&2
    exit 2
fi
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail=0

# type | member  (member checked as a whole-word token in the decompiled type source)
CHECKS="
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_cardSize
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_needsReinit
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|ConnectSignals
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_ExitTree
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|_Process
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|CardPadding
MegaCrit.Sts2.Core.Nodes.Cards.NCardGrid|CurrentlyDisplayedCardHolders
MegaCrit.Sts2.Core.Nodes.GodotExtensions.NClickableControl|_isHovered
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|_isHovered
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|_isFocused
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|_hoverTween
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|RefreshFocusState
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|SmallScale
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NCardHolder|Hitbox
MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder|Create
MegaCrit.Sts2.Core.Nodes.Screens.NCardsViewScreen|ConnectSignals
MegaCrit.Sts2.Core.Nodes.Screens.NCardsViewScreen|_showUpgrades
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_Process
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|Open
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|SetTravelEnabled
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|RecalculateTravelability
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|IsTravelEnabled
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_mapPointDictionary
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_runState
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_marker
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint|Point
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapPoint|State
MegaCrit.Sts2.Core.Nodes.Screens.Map.NNormalMapPoint|_icon
MegaCrit.Sts2.Core.Nodes.Screens.Map.NAncientMapPoint|_icon
MegaCrit.Sts2.Core.Nodes.Screens.Map.NBossMapPoint|_placeholderImage
MegaCrit.Sts2.Core.Map.MapPoint|coord
MegaCrit.Sts2.Core.Map.MapPoint|PointType
MegaCrit.Sts2.Core.Map.MapPoint|Children
MegaCrit.Sts2.Core.Runs.RunState|CurrentMapCoord
MegaCrit.Sts2.Core.Runs.RunState|CurrentActIndex
MegaCrit.Sts2.Core.Runs.RunState|ActFloor
MegaCrit.Sts2.Core.Runs.RunState|Act
MegaCrit.Sts2.Core.Runs.RunState|MapPointHistory
MegaCrit.Sts2.Core.Models.ActModel|Title
MegaCrit.Sts2.Core.Localization.LocString|GetFormattedText
MegaCrit.Sts2.Core.ControllerInput.NInputManager|ProcessShortcutKeyInput
MegaCrit.Sts2.Core.ControllerInput.NControllerManager|IsUsingController
"

decompile() { # $1 = type ; caches to $TMP/<type>.cs
    local t="$1" f="$TMP/$1.cs"
    [ -s "$f" ] && return 0
    "$ILSPY" -t "$t" "$DLL" >"$f" 2>/dev/null
}

for line in $CHECKS; do
    [ -z "$line" ] && continue
    type="${line%%|*}"; member="${line##*|}"
    decompile "$type"
    f="$TMP/$type.cs"
    if [ ! -s "$f" ]; then
        printf 'FAIL  %-55s (type did not decompile)\n' "$type"
        fail=1; continue
    fi
    hit="$(rg -m1 -w "$member" "$f" 2>/dev/null | sed 's/^[[:space:]]*//')"
    if [ -n "$hit" ]; then
        printf 'PASS  %-40s %-26s | %s\n' "${type##*.}" "$member" "$hit"
    else
        printf 'FAIL  %-40s %-26s | NOT FOUND\n' "${type##*.}" "$member"
        fail=1
    fi
done

# NGridCardHolder must NOT override SmallScale (our SmallScale patch targets base NCardHolder).
gf="$TMP/MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder.cs"
decompile "MegaCrit.Sts2.Core.Nodes.Cards.Holders.NGridCardHolder"
if rg -q "override .*\\bSmallScale\\b" "$gf" 2>/dev/null; then
    printf 'FAIL  %-40s %-26s | NGridCardHolder OVERRIDES SmallScale (patch would miss it)\n' "NGridCardHolder" "!SmallScale-override"
    fail=1
else
    printf 'PASS  %-40s %-26s | not overridden (base patch applies)\n' "NGridCardHolder" "SmallScale-not-overridden"
fi

echo
if [ "$fail" -eq 0 ]; then echo "ALL HOOKS PRESENT"; else echo "SOME HOOKS MISSING — fix before shipping"; fi
exit "$fail"
