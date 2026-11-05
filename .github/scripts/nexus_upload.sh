# Sourced by .github/workflows/release.yml: upload <file> <url> to the Nexus
# raw repository. Nexus answers 409 when a file already exists and redeploy is
# disabled (latest/..., or a re-run of the same version) - then delete it and
# upload again. Needs NEXUS_USER / NEXUS_PASS.
upload() {
  local code
  code=$(curl -sS -o /dev/null -w '%{http_code}' -u "$NEXUS_USER:$NEXUS_PASS" \
    -H "Content-Type: application/octet-stream" --upload-file "$1" "$2")
  if [ "$code" = 409 ]; then
    curl -sSf -u "$NEXUS_USER:$NEXUS_PASS" -X DELETE "$2" -o /dev/null
    code=$(curl -sS -o /dev/null -w '%{http_code}' -u "$NEXUS_USER:$NEXUS_PASS" \
      -H "Content-Type: application/octet-stream" --upload-file "$1" "$2")
  fi
  case "$code" in
    2??) echo "OK  $2" ;;
    *) echo "::error::Upload $2 -> HTTP $code"; return 1 ;;
  esac
}
