#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_BASE="$REPO_ROOT/publish-local"

cd "$REPO_ROOT"

echo "Output root: $OUT_BASE"
echo ""

# --- Server ---
for RID in win-x64 linux-x64; do
    for SC in true false; do
        SUFFIX=$([[ "$SC" == "true" ]] && echo "selfcontained" || echo "framework")
        OUTDIR="$OUT_BASE/OpenFreq-Server-${RID}-${SUFFIX}"
        SC_FLAGS=$([[ "$SC" == "true" ]] && echo "-p:PublishSingleFile=true -p:PublishTrimmed=true" || echo "")

        echo ">>> Server | $RID | $SUFFIX"
        dotnet publish OpenFreq.Server/OpenFreq.Server.csproj \
            -c Release -r "$RID" --self-contained "$SC" \
            $SC_FLAGS \
            -p:DebugType=none -p:DebugSymbols=false \
            -o "$OUTDIR"
        echo "    -> $OUTDIR ($(find "$OUTDIR" -type f | wc -l) files)"
        echo ""
    done
done

# --- Client ---
for RID in win-x64 linux-x64; do
    for SC in true false; do
        SUFFIX=$([[ "$SC" == "true" ]] && echo "selfcontained" || echo "framework")
        OUTDIR="$OUT_BASE/OpenFreq-Client-${RID}-${SUFFIX}"
        SC_FLAGS=$([[ "$SC" == "true" ]] && echo "-p:PublishSingleFile=true -p:PublishTrimmed=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true" || echo "")

        echo ">>> Client | $RID | $SUFFIX"
        dotnet publish OpenFreq.Client/OpenFreq.Client.csproj \
            -c Release -r "$RID" --self-contained "$SC" \
            $SC_FLAGS \
            -p:DebugType=none -p:DebugSymbols=false \
            -o "$OUTDIR"

        if [[ "$RID" == "win-x64" ]]; then
            cp libs/OpenFreqAudio/OpenFreqAudio/Assets/windows/*.dll "$OUTDIR/"
        else
            cp libs/OpenFreqAudio/OpenFreqAudio/Assets/linux/*.so "$OUTDIR/"
        fi

        echo "    -> $OUTDIR ($(find "$OUTDIR" -type f | wc -l) files)"
        echo ""
    done
done

echo "Done. Artifacts:"
for DIR in "$OUT_BASE"/*/; do
    COUNT=$(find "$DIR" -type f | wc -l)
    printf "  %-55s %d files\n" "$(basename "$DIR")" "$COUNT"
done
