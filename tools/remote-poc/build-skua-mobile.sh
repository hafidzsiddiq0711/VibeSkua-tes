#!/bin/bash
# Rebuild tools/remote-poc/skua-mobile.swf (skua.swf with the SocketBridge transport) without Flex:
# uses Ruffle's ActionScript compiler (tools/asc/asc.jar in the Ruffle repo) and the
# playerglobal_import.abc that Ruffle's build script generates (target/*/build/ruffle_core-*/out/).
#   usage: build-skua-mobile.sh <asc.jar> <playerglobal_import.abc>
set -e
ASC="$1"; ABC="$2"
here="$(cd "$(dirname "$0")" && pwd)"; src="$here/../../Skua.AS3/skua/src"
tmp="$(mktemp -d)"; cp -r "$src" "$tmp/src"; cd "$tmp"
ORDER=$( { echo src/skua/module/Module.as; find src -name "*.as" ! -path "*/skua/Main.as" ! -path "*/module/Module.as" | sort; } )
FILES=$(for f in $ORDER; do printf -- "-in %s " "$f"; done)
java -jar "$ASC" -AS3 -strict -optimize -import "$ABC" $FILES -swf skua.Main,958,550,30 src/skua/Main.as
python3 "$here/finish-swf.py" src/skua/Main.swf "$here/skua-mobile.swf"
