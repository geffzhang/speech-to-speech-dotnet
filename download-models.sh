#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
models_dir="$script_dir/models"
manifest="$script_dir/tools/models/models.tsv"
groups_csv=
groups_set=false

fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }
usage() {
    printf 'Usage: bash download-models.sh [--models-dir DIR] [--manifest TSV] [--groups LIST]\n'
}
while (($#)); do
    case "$1" in
        --models-dir|--manifest|--groups)
            (($# >= 2)) || fail "Missing value for $1"
            case "$1" in
                --models-dir) models_dir="$2" ;;
                --manifest) manifest="$2" ;;
                --groups) groups_csv="$2"; groups_set=true ;;
            esac
            shift 2 ;;
        --help|-h) usage; exit 0 ;;
        *) usage >&2; fail "Unknown argument: $1" ;;
    esac
done

groups=()
if [[ "$groups_set" == true ]]; then
    [[ -n "$groups_csv" && "$groups_csv" != ,* && "$groups_csv" != *, && "$groups_csv" != *,,* ]] ||
        fail "Invalid group filter: $groups_csv"
    IFS=',' read -r -a groups <<< "$groups_csv"
    checked_groups=()
    for group in "${groups[@]}"; do
        case "$group" in
            vad|whisper|sense-voice|paraformer|llm|tts|smart-turn) ;;
            *) fail "Invalid group filter: $group" ;;
        esac
        for checked in "${checked_groups[@]+"${checked_groups[@]}"}"; do
            [[ "$group" != "$checked" ]] || fail "Invalid group filter: $group"
        done
        checked_groups+=("$group")
    done
fi

command -v curl >/dev/null || fail "curl is required"
if command -v sha256sum >/dev/null; then
    hash_command=(sha256sum)
elif command -v shasum >/dev/null; then
    hash_command=(shasum -a 256)
else
    fail "sha256sum or shasum is required"
fi
[[ -r "$manifest" ]] || fail "Cannot read manifest: $manifest"

hashes=() sizes=() paths=() urls=() keys=()
path_pattern='^[a-zA-Z0-9_-][a-zA-Z0-9._-]*(/[a-zA-Z0-9_-][a-zA-Z0-9._-]*)*$'
url_pattern='^https?://[^[:space:]]+$'
while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ -z "$line" || "$line" == \#* ]] && continue
    IFS=$'\t' read -r sha size relative url extra <<< "$line"
    # Validate delimiters as well: read treats adjacent tabs as one separator.
    tabs="${line//[^$'\t']/}"
    [[ ${#tabs} == 3 && "$sha" =~ ^[a-fA-F0-9]{64}$ &&
       "$size" =~ ^[1-9][0-9]{0,14}$ && "$relative" =~ $path_pattern &&
       "$url" =~ $url_pattern ]] || fail "Invalid manifest entry: $line"
    key="$(printf '%s' "$relative" | tr '[:upper:]' '[:lower:]')"
    [[ "$key" != *.part ]] || fail "Reserved partial-file path: $relative"
    for existing in "${keys[@]+"${keys[@]}"}"; do
        [[ "$existing" != "$key" && "$existing" != "$key/"* && "$key" != "$existing/"* ]] ||
            fail "Duplicate or conflicting manifest path: $relative"
    done
    hashes+=("$(printf '%s' "$sha" | tr '[:upper:]' '[:lower:]')")
    sizes+=("$size") paths+=("$relative") urls+=("$url") keys+=("$key")
done < "$manifest"
((${#paths[@]} > 0)) || fail "Empty model manifest"

if [[ "$groups_set" == true ]]; then
    for group in "${groups[@]}"; do
        found=false
        for relative in "${paths[@]}"; do
            if [[ "${relative%%/*}" == "$group" ]]; then found=true; break; fi
        done
        [[ "$found" == true ]] || fail "Invalid group filter: no manifest entries for $group"
    done

    filtered_hashes=() filtered_sizes=() filtered_paths=() filtered_urls=()
    for ((i = 0; i < ${#paths[@]}; i++)); do
        for group in "${groups[@]}"; do
            if [[ "${paths[$i]%%/*}" == "$group" ]]; then
                filtered_hashes+=("${hashes[$i]}")
                filtered_sizes+=("${sizes[$i]}")
                filtered_paths+=("${paths[$i]}")
                filtered_urls+=("${urls[$i]}")
                break
            fi
        done
    done
    hashes=("${filtered_hashes[@]}")
    sizes=("${filtered_sizes[@]}")
    paths=("${filtered_paths[@]}")
    urls=("${filtered_urls[@]}")
fi

file_size() { wc -c < "$1" | tr -d '[:space:]'; }
verified() {
    [[ -f "$1" ]] || return 1
    [[ "$(file_size "$1")" == "$2" ]] || return 1
    local digest
    digest="$("${hash_command[@]}" "$1")" || return 1
    [[ "${digest%% *}" == "$3" ]]
}

for ((i = 0; i < ${#paths[@]}; i++)); do
    relative="${paths[$i]}" size="${sizes[$i]}" sha="${hashes[$i]}"
    target="$models_dir/$relative"
    partial="$target.part"
    if verified "$target" "$size" "$sha"; then
        printf '[verified] %s\n' "$relative"
        continue
    fi
    [[ ! -f "$target" ]] || printf 'Warning: replacing corrupt model: %s\n' "$relative" >&2
    mkdir -p -- "$(dirname -- "$target")"
    if ! verified "$partial" "$size" "$sha"; then
        if [[ -f "$partial" ]] && (("$(file_size "$partial")" >= size)); then
            printf 'Warning: discarding invalid completed partial: %s\n' "$relative" >&2
            rm -- "$partial"
        fi
        printf '[download/resume] %s\n' "$relative"
        curl_args=(
            --fail --location --show-error --retry 3
            --connect-timeout 30 --speed-limit 1024 --speed-time 60
            --proto '=http,https' --proto-redir '=http,https'
            --output "$partial" --url "${urls[$i]}"
        )
        code=0
        curl "${curl_args[@]}" --continue-at - || code=$?
        if ((code == 33)); then
            printf 'Warning: server does not support resuming; restarting: %s\n' "$relative" >&2
            code=0
            curl "${curl_args[@]}" || code=$?
        fi
        ((code == 0)) || fail "curl exited $code for $relative. Partial retained; rerun to resume."
        if ! verified "$partial" "$size" "$sha"; then
            rm -- "$partial"
            fail "SHA-256/size mismatch: $relative. Invalid partial removed; rerun to download again."
        fi
    fi
    mv -f -- "$partial" "$target"
    printf '[verified] %s\n' "$relative"
done
printf 'All %s model files verified: %s\n' "${#paths[@]}" "$models_dir"
