#!/usr/bin/env bash
# Deploy and run the host side on IBM i over SSH:
#   1. export the data slice from the raw M5 files into data/
#   2. upload the source and the slice
#   3. create the reference tables if the library lacks them
#   4. compile LOADSLICE, RUNCHAIN and FCSTNAIVE
#   5. run RUNCHAIN (load, then the fallback forecast and the suggested rows)
#   6. pull FORECAST and SUGGESTED back into results/
#
#   tools/deploy.sh <m5-data-dir>
#
# Needs IBMI_HOST, IBMI_USER and IBMI_LIBRARY, and an SSH key the host accepts.
# The SSH port is IBMI_PORT, default 2222 (PUB400). The full host output goes to
# results/deploy.log.
set -euo pipefail

for v in IBMI_HOST IBMI_USER IBMI_LIBRARY; do
    if [[ -z "${!v:-}" ]]; then
        echo "deploy: environment variable $v is not set" >&2
        exit 1
    fi
done
if [[ $# -ne 1 ]]; then
    echo "usage: tools/deploy.sh <m5-data-dir>" >&2
    exit 1
fi

M5_DIR=$1
LIB=$IBMI_LIBRARY
TARGET=$IBMI_USER@$IBMI_HOST
PORT=${IBMI_PORT:-2222}
# LogLevel=ERROR hides the host's login banner.
SSH_OPTS=(-o BatchMode=yes -o LogLevel=ERROR)

cd "$(dirname "$0")/.."
mkdir -p results
LOG=results/deploy.log
: > "$LOG"

host() {  # run a shell command on the host, output to the log
    echo "+ $*" >> "$LOG"
    ssh "${SSH_OPTS[@]}" -p "$PORT" "$TARGET" "$@" >> "$LOG" 2>&1
}

cl() {  # run one CL command on the host, with LIB on the library list
    host "/QOpenSys/usr/bin/qsh -c \"liblist -a $LIB; system -kKe \\\"$1\\\"\""
}

step() { echo "deploy: $*"; }

fail() {
    echo "deploy: failed. Last lines of $LOG:" >&2
    tail -20 "$LOG" >&2
    exit 1
}
trap fail ERR

step "exporting the slice from $M5_DIR"
PYTHON=python3
[[ -x .venv/bin/python ]] && PYTHON=.venv/bin/python
"$PYTHON" tools/export_slice.py "$M5_DIR" --out data

step "uploading source and data"
REMOTE_HOME=$(ssh "${SSH_OPTS[@]}" -p "$PORT" "$TARGET" 'echo $HOME')
DIR=$REMOTE_HOME/forecast-host-ports
host "mkdir -p $DIR/ibmi/schema $DIR/ibmi/cl $DIR/ibmi/rpg $DIR/data $DIR/results"
sftp "${SSH_OPTS[@]}" -P "$PORT" -b - "$TARGET" >> "$LOG" 2>&1 <<EOF
put ibmi/schema/*.sql $DIR/ibmi/schema/
put ibmi/cl/*.clle $DIR/ibmi/cl/
put ibmi/rpg/*.sqlrpgle $DIR/ibmi/rpg/
put data/*.csv $DIR/data/
EOF

step "checking tables in $LIB"
for t in SALES FORECAST SUGGESTED; do
    if ! cl "CHKOBJ OBJ($LIB/$t) OBJTYPE(*FILE)"; then
        echo "deploy: $LIB/$t is missing; create it from ibmi/schema/forecast_tables.sql" >&2
        exit 1
    fi
done
if ! cl "CHKOBJ OBJ($LIB/STORE) OBJTYPE(*FILE)"; then
    step "creating the reference tables"
    # ERRLVL(20): every CREATE warns SQL7905 (not journaled) at severity 20.
    cl "RUNSQLSTM SRCSTMF('$DIR/ibmi/schema/reference_tables.sql') COMMIT(*NONE) NAMING(*SQL) DFTRDBCOL($LIB) ERRLVL(20)"
fi

step "compiling"
cl "CRTBNDCL PGM($LIB/LOADSLICE) SRCSTMF('$DIR/ibmi/cl/loadslice.clle') REPLACE(*YES)"
cl "CRTSQLRPGI OBJ($LIB/FCSTNAIVE) SRCSTMF('$DIR/ibmi/rpg/fcstnaive.sqlrpgle') OBJTYPE(*PGM) COMMIT(*NONE) OPTION(*SYS) CVTCCSID(*JOB) DBGVIEW(*SOURCE) REPLACE(*YES)"
cl "CRTBNDCL PGM($LIB/RUNCHAIN) SRCSTMF('$DIR/ibmi/cl/runchain.clle') REPLACE(*YES)"

step "running RUNCHAIN"
# A CL CALL passes a literal at its own length; pad DIR to the declared 128 bytes.
DATA=$(printf '%-128s' "$DIR/data")
cl "CALL PGM($LIB/RUNCHAIN) PARM('$LIB' '$DATA')"

step "pulling FORECAST and SUGGESTED into results/"
for t in FORECAST SUGGESTED; do
    cl "CPYTOIMPF FROMFILE($LIB/$t) TOSTMF('$DIR/results/$t.csv') MBROPT(*REPLACE) STMFCCSID(1208) RCDDLM(*LF) DTAFMT(*DLM) STRDLM(*DBLQUOTE) FLDDLM(',') DATFMT(*ISO) ADDCOLNAM(*SQL)"
done
sftp "${SSH_OPTS[@]}" -P "$PORT" -b - "$TARGET" >> "$LOG" 2>&1 <<EOF
get $DIR/results/FORECAST.csv results/
get $DIR/results/SUGGESTED.csv results/
EOF

for t in FORECAST SUGGESTED; do
    step "results/$t.csv: $(($(wc -l < "results/$t.csv") - 1)) rows"
done
step "done"
