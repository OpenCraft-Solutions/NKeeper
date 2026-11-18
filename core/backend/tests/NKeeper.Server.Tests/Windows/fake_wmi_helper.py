"""Stands in for core's wmi_helper.py (docs/architecture.md §7.28): the same JSON lines, without Windows.

The login "bad" is refused; "WHOAMI" answers who signed in (the password only by its length); "REFUSE" is a query
the host refuses; "DIE" ends the helper; anything else answers five rows, newest first, honouring the limit and since.
"""
import json
import sys


def send(message):
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


login = json.loads(sys.stdin.readline())
if login["username"] == "bad":
    send({"error": "Windows refused the login.", "kind": "auth"})
    sys.exit(1)
send({"ready": True})

for line in sys.stdin:
    query = json.loads(line)
    wql = query["wql"]
    if wql == "WHOAMI":
        send({"rows": [{"host": login["host"], "username": login["username"], "domain": login.get("domain"), "passwordLength": len(login["password"]),
                        "namespace": query["namespace"]}]})
    elif wql == "REFUSE":
        send({"error": "WMI failed: WBEM_E_INVALID_QUERY", "kind": "query"})
    elif wql == "DIE":
        sys.exit(3)
    else:
        rows = []
        for n in range(5):
            stamp = "202611{:02d}000000".format(10 - n)
            if query.get("since") and stamp < query["since"]:
                break
            if len(rows) >= query["limit"]:
                break
            rows.append({"n": n, "TimeGenerated": stamp + ".000000-000"})
        send({"rows": rows})
