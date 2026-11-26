#!/bin/sh
# Report the packages actually installed in the finished Debian/Ubuntu image.
set -eu
packages=$(dpkg-query -W -f='${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\t${Architecture}\t${db:Status-Status}\n')
printf '%s\n' "$packages" | awk -F '\t' 'BEGIN { OFS="\t" } $6 == "installed" { print $1, $2, $3, $4, $5 }'
