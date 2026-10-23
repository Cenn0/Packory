#!/usr/bin/env bash
# Shared classifier for disposable public-tooling bootstrap only. Never retries
# Packory protocol assertions, missing artifacts, or compilation/validation failures.
packory_maven_transient() {
  grep -Eiq 'Connection reset|Read timed out|Connection timed out|Temporary failure|Network is unreachable|429 Too Many Requests|502 Bad Gateway|503 Service Unavailable|504 Gateway Timeout|(HTTP Status|status code|status)[[:space:]]*:[[:space:]]*(429|502|503|504)([^0-9]|$)' "$1"
}
packory_maven_backoff() {
  case "$1" in
    1) sleep 15 ;;
    2) sleep 30 ;;
    *) return 2 ;;
  esac
}
