"""Read-only WMI queries over DCOM for NKeeper's WMI connections (docs/architecture.md §7.28), with Impacket.

Ported from 1.x's wmi_scan.py and wmi_event_log.py. Core starts one helper per connection and speaks JSON lines:

- the first line, on stdin, is the login: {"host", "username", "password", "domain"}. It never goes on the command
  line. The helper signs in and answers {"ready": true}, or {"error", "kind"} and exits;
- each later line is a query: {"namespace", "wql", "limit", "timeProperty", "since"}, answered by {"rows": [...]} or
  {"error", "kind": "query"}. "since" is UTC, yyyymmddhhmmss: reading stops at the first row whose timeProperty is
  older, since event logs list newest first;
- the helper signs out and exits when stdin closes.
"""
from datetime import datetime, timedelta
import json
import sys


def send(message):
    sys.stdout.write(json.dumps(message, default=str) + "\n")
    sys.stdout.flush()


def failure(ex):
    """What went wrong, without the login: (kind, message)."""
    text = str(ex)
    lowered = text.lower()
    if "logon_failure" in lowered or "access_denied" in lowered or "accessdenied" in lowered:
        return "auth", "Windows refused the login. Check the username, password and domain, and that the account may use remote WMI (DCOM)."
    if any(s in lowered for s in ("connection refused", "timed out", "unreachable", "no route", "name or service not known", "connection reset")):
        return "connect", "Couldn't reach the host over RPC. Check that it is up and its firewall allows WMI (DCOM, port 135)."
    return "error", "WMI failed: " + text


try:
    from impacket.dcerpc.v5.dcom import wmi
    from impacket.dcerpc.v5.dcomrt import DCOMConnection
    from impacket.dcerpc.v5.dtypes import NULL
except ImportError:
    send({"error": "WMI needs Impacket for Python on the NKeeper server (the python3-impacket package).", "kind": "missing"})
    sys.exit(1)


def utc(dmtf):
    """A CIM datetime (20261106182947.371480-000) in UTC, or None."""
    try:
        minutes = int(dmtf[21:])
        return datetime.strptime(dmtf[:14], "%Y%m%d%H%M%S") - timedelta(minutes=minutes if dmtf[21] == "+" else -minutes)
    except (TypeError, ValueError, IndexError):
        return None


def rows(services, query):
    limit = query.get("limit")
    since = datetime.strptime(query["since"], "%Y%m%d%H%M%S") if query.get("since") else None
    time_property = query.get("timeProperty")
    # Without these flags the host finishes the whole query before answering.
    enum = services.ExecQuery(query["wql"], wmi.WBEM_FLAG_RETURN_IMMEDIATELY | wmi.WBEM_FLAG_FORWARD_ONLY)
    result = []
    while limit is None or len(result) < limit:
        try:
            item = enum.Next(0xFFFFFFFF, 1)[0]
        except Exception as ex:  # impacket signals the end of the set with S_FALSE
            if "S_FALSE" in str(ex):
                break
            raise
        row = {name: prop["value"] for name, prop in item.getProperties().items()}
        if since is not None and time_property:
            generated = utc(row.get(time_property))
            if generated is not None and generated < since:
                break
        result.append(row)
    return result


def main():
    login_request = json.loads(sys.stdin.readline())
    dcom = None
    try:
        dcom = DCOMConnection(
            login_request["host"], login_request["username"], login_request["password"], login_request.get("domain") or "", "", "", None, oxidResolver=True)
        login = wmi.IWbemLevel1Login(dcom.CoCreateInstanceEx(wmi.CLSID_WbemLevel1Login, wmi.IID_IWbemLevel1Login))
        namespaces = {}
        namespaces["root/cimv2"] = login.NTLMLogin("//./root/cimv2", NULL, NULL)
    except Exception as ex:
        kind, message = failure(ex)
        send({"error": message, "kind": kind})
        if dcom is not None:
            try:
                dcom.disconnect()
            except Exception:
                pass
        sys.exit(1)

    del login_request
    send({"ready": True})
    try:
        for line in sys.stdin:
            if not line.strip():
                continue
            query = json.loads(line)
            try:
                namespace = query.get("namespace") or "root/cimv2"
                if namespace not in namespaces:
                    namespaces[namespace] = login.NTLMLogin("//./" + namespace, NULL, NULL)
                send({"rows": rows(namespaces[namespace], query)})
            except Exception as ex:
                kind, message = failure(ex)
                send({"error": message, "kind": "connect" if kind == "connect" else "query"})
    finally:
        try:
            dcom.disconnect()
        except Exception:
            pass


if __name__ == "__main__":
    main()
