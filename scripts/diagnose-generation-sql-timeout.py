#!/usr/bin/env python3
"""Read-only psql runtime and orchestration state check.

Does not print connection strings, credentials, SQL errors, provider access
details, user data, or container environment variables.
"""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import time

def database(env_file):
    source = Path(env_file).read_text()
    line = next((item.partition("=")[2] for item in source.splitlines()
                 if item.startswith("AGENT_POSTGRES_CONNECTION_STRING=")), "")
    parts = dict(chunk.split("=",1) for chunk in line.split(";") if "=" in chunk)
    if any(not parts.get(key) for key in ("Database","Username","Password")):
        raise ValueError("Missing protected DB settings")
    return parts

def probe(image, db, sql):
    start=time.monotonic()
    env=os.environ.copy()
    env["PGPASSWORD"]=db["Password"]
    cmd=["docker","run","--pull=never","--rm","--network","dhole",
         "-e","PGPASSWORD","-e","PGCONNECT_TIMEOUT=4",
         image,"psql","-X","-q","-At","-v","ON_ERROR_STOP=1",
         "-h",db.get("Host","postgres"),"-p",db.get("Port","5432"),
         "-U",db["Username"],"-d",db["Database"],
         "-c", "SET statement_timeout='4s'; "+sql]
    try:
        result=subprocess.run(cmd,capture_output=True,text=True,timeout=14,env=env)
    except subprocess.TimeoutExpired:
        print("GENERATION_SQL_DIAG image="+image+" result=timeout elapsed_sec="+str(int(time.monotonic()-start)))
        return
    except (OSError,ValueError):
        print("GENERATION_SQL_DIAG image="+image+" result=runtime_error")
        return
    elapsed=int(1000*(time.monotonic()-start))
    print("GENERATION_SQL_DIAG image="+image+" result="+("ok" if result.returncode==0 else "error")
          +" elapsed_ms="+str(elapsed))
    if result.returncode==0 and sql.startswith("SELECT CASE"):
        print("GENERATION_SQL_DIAG_REQUEST_STATUS="+result.stdout.strip()[:240])

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--env-file",required=True)
    args=parser.parse_args()
    db=database(args.env_file)
    for image in ("postgres:16-alpine","postgres:17-alpine"):
        result=subprocess.run(["docker","image","inspect",image],
                              capture_output=True,timeout=8)
        print("GENERATION_SQL_IMAGE image="+image+" cached="+str(result.returncode==0).lower())
        probe(image,db,"SELECT 1;")
    # Sanitized status values only, never request UUID, actor or reason.
    status_sql=(
       "SELECT CASE WHEN EXISTS(SELECT 1 FROM agent.runtime_generation_requests "
       "WHERE status='Processing' AND created_at_utc < now()-interval '5 minutes') "
       "THEN 'stale_processing' ELSE 'no_stale_processing' END;")
    probe("postgres:17-alpine",db,status_sql)
    print("GENERATION_SQL_DIAGNOSTIC_READ_ONLY_FINISHED")

if __name__=="__main__":
    try:
        main()
    except (OSError,ValueError,subprocess.SubprocessError):
        print("GENERATION_SQL_DIAG_CONFIG_OR_RUNTIME_ERROR",file=sys.stderr)
        sys.exit(1)
