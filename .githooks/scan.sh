#!/usr/bin/env bash
# Skaner blokujący commit/push, jeśli w plikach są sekrety albo pliki, które nie powinny trafić do repo.
# Użycie: scan.sh <ref> <plik>...   — <ref> to ":" (indeks, pre-commit) albo SHA commita (pre-push).
# Świadome obejście (np. fałszywy alarm): git commit --no-verify / git push --no-verify.

ref="$1"
shift

max_bytes=$((5 * 1024 * 1024))
failed=0

# Pliki, które nie powinny być w repozytorium niezależnie od zawartości.
forbidden_path='(^|/)(bin|obj|\.vs)/|\.(pfx|p12|snk|pem|key|cer|crt|db|db-wal|db-shm|sqlite|user|suo)$|(^|/)\.env(\..*)?$|(^|/)(secrets|settings)\.json$|launchSettings\.json$|\.pubxml\.user$'

# Wzorce sekretów w treści.
secret_patterns=(
  '-----BEGIN ([A-Z]+ )?PRIVATE KEY-----'
  'gh[pousr]_[A-Za-z0-9]{36,}'
  'github_pat_[A-Za-z0-9_]{40,}'
  'AKIA[0-9A-Z]{16}'
  'sk-ant-[A-Za-z0-9_-]{20,}'
  'sk-[A-Za-z0-9]{32,}'
  'xox[abpors]-[A-Za-z0-9-]{10,}'
  'AIza[0-9A-Za-z_-]{35}'
  'AccountKey=[A-Za-z0-9+/=]{20,}'
  'SharedAccessSignature=[A-Za-z0-9%+/=&]{20,}'
  '[?&]sig=[A-Za-z0-9%+/=]{20,}'
  '(password|passwd|pwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)["'"'"']?[[:space:]]*[:=][[:space:]]*["'"'"'][^"'"'"'[:space:]]{8,}["'"'"']'
)

report() {
  echo "  ✗ $1" >&2
  failed=1
}

# Jeden grep na plik ze wszystkimi wzorcami — na Windows każdy proces jest drogi.
grep_args=()
for pattern in "${secret_patterns[@]}"; do
  grep_args+=(-e "$pattern")
done

shopt -s nocasematch
for path in "$@"; do
  [ -z "$path" ] && continue

  if [[ $path =~ $forbidden_path ]]; then
    report "$path — typ pliku zablokowany (build/IDE/klucz/certyfikat/baza/sekrety)"
    continue
  fi

  if [ "$ref" = ":" ]; then spec=":$path"; else spec="$ref:$path"; fi
  size=$(git cat-file -s "$spec" 2>/dev/null) || continue
  if [ "$size" -gt "$max_bytes" ]; then
    report "$path — plik ma $((size / 1024 / 1024)) MB (limit 5 MB)"
    continue
  fi

  # -I: pliki binarne (obrazy itp.) są pomijane w skanie treści.
  match=$(git cat-file -p "$spec" | grep -nEIi -m1 "${grep_args[@]}")
  if [ -n "$match" ]; then
    line=${match%%:*}
    # Samej wartości nie wypisujemy, żeby sekret nie trafił do logów terminala.
    report "$path:$line — wygląda na sekret (klucz/token/hasło)"
  fi
done

if [ "$failed" -ne 0 ]; then
  echo "" >&2
  echo "Zablokowano: usuń te elementy (albo dodaj je do .gitignore) i spróbuj ponownie." >&2
  echo "Jeśli to fałszywy alarm: --no-verify." >&2
  exit 1
fi
exit 0
