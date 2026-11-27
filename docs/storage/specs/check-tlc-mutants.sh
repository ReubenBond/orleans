#!/usr/bin/env bash
# Runs every `action tlcMutant*` in a Quint spec with TLC and requires each to
# violate an invariant. Companion to check-mutants.sh, for mutants whose
# violations random simulation rarely reaches.
#
# Usage: check-tlc-mutants.sh <spec.qnt> <main-module> <default-invariant> [extra quint verify args...]
#
# As in check-mutants.sh, `// breaks: <invariant>` on the line directly above a
# mutant names the invariant it must break, and a mutant counts as caught only
# when quint reports `[violation]` and TLC reports the invariant violated. Any
# other failure (unknown step, compile error, crash) fails the check.
set -uo pipefail

spec=$1 main=$2 default_inv=$3
shift 3

mapfile -t mutants < <(grep -oP '^\s*action\s+\KtlcMutant\w+' "$spec")
if [ ${#mutants[@]} -eq 0 ]; then
  echo "::error::no TLC mutants found in $spec"
  exit 1
fi

failed=0
for m in "${mutants[@]}"; do
  inv=$(grep -B1 -P "^\s*action\s+$m\b" "$spec" | grep -oP '//\s*breaks:\s*\K\w+' || true)
  inv=${inv:-$default_inv}
  out=$(quint verify "$spec" --main="$main" --backend=tlc --step="$m" --invariant="$inv" "$@" 2>&1)
  code=$?
  if [ $code -ne 0 ] && grep -q '\[violation\]' <<<"$out" && grep -q 'is violated' <<<"$out"; then
    echo "$m: violates $inv, as expected"
  elif [ $code -eq 0 ]; then
    echo "::error::$m was not caught by $inv"
    failed=1
  else
    echo "::error::$m failed without an invariant violation (exit $code):"
    echo "$out" | tail -n 20
    failed=1
  fi
done
exit $failed
