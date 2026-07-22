#!/usr/bin/env bash
# Verify every game member DeckView hooks by *string* (Harmony patch targets + AccessTools
# reflection) actually exists in the game's sts2.dll. The C# compiler already checks members
# accessed through typed APIs; it CANNOT check these string-named ones, so this is where a
# game update would silently break us (and, per our "work or crash" policy, crash at runtime).
# Run this after any game update to find exactly which member moved, before you even launch.
#
#   scripts/verify-hooks.sh
#
# Requires ilspycmd (see DEVELOPMENT.md). Exits non-zero if any member is missing.
set -u

ILSPY="${ILSPY:-/mnt/c/Users/ernes/.dotnet/tools/ilspycmd.exe}"
DLL="${STS2_DLL:-C:\\Program Files (x86)\\Steam\\steamapps\\common\\Slay the Spire 2\\data_sts2_windows_x86_64\\sts2.dll}"
TMP="$(mktemp -d)"
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
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_Process
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_mapPointDictionary
MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen|_runState
MegaCrit.Sts2.Core.Runs.RunState|CurrentMapCoord
MegaCrit.Sts2.Core.Nodes.Screens.NCardsViewScreen|ConnectSignals
MegaCrit.Sts2.Core.Nodes.Screens.NCardsViewScreen|_showUpgrades
MegaCrit.Sts2.Core.Nodes.CommonUi.NTickbox|IsTicked
MegaCrit.Sts2.Core.Nodes.CommonUi.NTickbox|Toggled
MegaCrit.Sts2.addons.mega_text.MegaLabel|SetTextAutoSize
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
    hit="$(grep -m1 -E "\b${member}\b" "$f" | sed 's/^[[:space:]]*//')"
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
if grep -qE "override .*\bSmallScale\b" "$gf" 2>/dev/null; then
    printf 'FAIL  %-40s %-26s | NGridCardHolder OVERRIDES SmallScale (patch would miss it)\n' "NGridCardHolder" "!SmallScale-override"
    fail=1
else
    printf 'PASS  %-40s %-26s | not overridden (base patch applies)\n' "NGridCardHolder" "SmallScale-not-overridden"
fi

echo
if [ "$fail" -eq 0 ]; then echo "ALL HOOKS PRESENT"; else echo "SOME HOOKS MISSING — fix before shipping"; fi
rm -rf "$TMP"
exit "$fail"
