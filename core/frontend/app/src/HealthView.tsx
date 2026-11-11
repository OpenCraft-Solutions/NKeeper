import { useEffect, useState } from "react";
import { api, ApiError } from "@nkeeper/sdk";
import type { Catalog } from "./catalog";
import { CERTIFICATE_WARNING_DAYS, daysUntil, expiryText } from "./health";
import { hostAddress, serviceName } from "./hosts";
import type { Host, HostService, ServiceHealth } from "./types";

const DOWN_REASONS: Record<string, string> = {
  refused: "Refused",
  timeout: "Timed out",
  unreachable: "Unreachable",
};
const timeFormat = new Intl.DateTimeFormat(undefined, {
  dateStyle: "medium",
  timeStyle: "short",
});
const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: "medium" });

interface Row {
  host: Host;
  service: HostService;
  check: ServiceHealth;
}

/**
 * The health of every service the user can see (docs/architecture.md §7.13, §7.14): what's down, and which
 * certificates have expired or soon will. Ported from 1.x's health report.
 */
export function HealthView({
  catalog,
  onShowHost,
}: {
  catalog: Catalog;
  onShowHost: (hostId: string) => void;
}) {
  const [data, setData] = useState<{
    hosts: Host[];
    health: ServiceHealth[];
  } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [reloads, setReloads] = useState(0);

  useEffect(() => {
    let cancelled = false;
    Promise.all([
      api.get<Host[]>("/api/hosts"),
      api.get<ServiceHealth[]>("/api/health"),
    ]).then(
      ([hosts, health]) => {
        if (!cancelled) {
          setData({ hosts, health });
          setError(null);
          setLoading(false);
        }
      },
      (failure: unknown) => {
        if (!cancelled) {
          setError(
            failure instanceof ApiError
              ? failure.message
              : "The health results could not be loaded.",
          );
          setLoading(false);
        }
      },
    );
    return () => {
      cancelled = true;
    };
  }, [reloads]);

  if (!data) {
    return error ? (
      <p className="nk-alert nk-pad" role="alert">
        {error}
      </p>
    ) : (
      <p className="nk-muted nk-pad">Loading…</p>
    );
  }

  const byService = new Map(data.health.map((c) => [c.serviceId, c]));
  const rows: Row[] = data.hosts.flatMap((host) =>
    host.services.flatMap((service) => {
      const check = byService.get(service.id);
      return check ? [{ host, service, check }] : [];
    }),
  );
  const down = rows
    .filter((r) => r.check.state === "down")
    .sort((a, b) => a.check.checkedAtUtc.localeCompare(b.check.checkedAtUtc));
  const certificates = rows
    .filter(
      (r): r is Row & { check: { certificateExpiresAtUtc: string } } =>
        !!r.check.certificateExpiresAtUtc,
    )
    .sort((a, b) =>
      a.check.certificateExpiresAtUtc.localeCompare(
        b.check.certificateExpiresAtUtc,
      ),
    );
  const expiring = certificates.filter(
    (r) =>
      daysUntil(r.check.certificateExpiresAtUtc) <= CERTIFICATE_WARNING_DAYS,
  ).length;
  const unchecked = data.hosts.filter(
    (h) =>
      h.services.length > 0 && !h.services.some((s) => byService.has(s.id)),
  ).length;
  const latest = rows.reduce<string | null>(
    (last, r) =>
      last === null || r.check.checkedAtUtc > last
        ? r.check.checkedAtUtc
        : last,
    null,
  );

  const name = (service: HostService) =>
    serviceName(service, catalog.serviceTypes);
  const address = (row: Row) => {
    const port =
      row.service.port ??
      catalog.serviceTypes.get(row.service.type)?.defaultPort;
    return `${row.service.address ?? hostAddress(row.host) ?? "—"}${port ? `:${port}` : ""}`;
  };
  const hostLink = (host: Host) => (
    <button
      type="button"
      className="nk-link"
      onClick={() => onShowHost(host.id)}
    >
      {host.name}
    </button>
  );

  return (
    <div className="nk-pad nk-health">
      <div className="nk-health-header">
        <div>
          <h2>Health</h2>
          <p className="nk-muted">
            {rows.length - down.length} of {rows.length} services up ·{" "}
            {down.length} down · {expiring}{" "}
            {expiring === 1 ? "certificate" : "certificates"} expiring within{" "}
            {CERTIFICATE_WARNING_DAYS} days
            {unchecked > 0
              ? ` · ${unchecked} ${unchecked === 1 ? "host" : "hosts"} not checked yet`
              : ""}
            {latest
              ? ` · last check ${timeFormat.format(new Date(latest))}`
              : ""}
          </p>
        </div>
        <button
          type="button"
          onClick={() => {
            setLoading(true);
            setReloads((n) => n + 1);
          }}
          disabled={loading}
        >
          Refresh
        </button>
      </div>
      {error && (
        <p className="nk-alert" role="alert">
          {error}
        </p>
      )}

      <section aria-labelledby="health-down">
        <h3 id="health-down">Down</h3>
        {down.length === 0 ? (
          <p className="nk-muted">Every checked service is answering.</p>
        ) : (
          <table className="nk-table">
            <thead>
              <tr>
                <th scope="col">Host</th>
                <th scope="col">Service</th>
                <th scope="col">Address</th>
                <th scope="col">Why</th>
                <th scope="col">Checked</th>
              </tr>
            </thead>
            <tbody>
              {down.map((row) => (
                <tr key={row.service.id}>
                  <td>{hostLink(row.host)}</td>
                  <td>{name(row.service)}</td>
                  <td className="nk-mono">{address(row)}</td>
                  <td className="nk-warning">
                    {row.check.reason
                      ? (DOWN_REASONS[row.check.reason] ?? row.check.reason)
                      : "Down"}
                  </td>
                  <td>{timeFormat.format(new Date(row.check.checkedAtUtc))}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section aria-labelledby="health-certificates">
        <h3 id="health-certificates">Certificates</h3>
        {certificates.length === 0 ? (
          <p className="nk-muted">No certificates have been read yet.</p>
        ) : (
          <table className="nk-table">
            <thead>
              <tr>
                <th scope="col">Host</th>
                <th scope="col">Service</th>
                <th scope="col">Issued to</th>
                <th scope="col">Expires</th>
              </tr>
            </thead>
            <tbody>
              {certificates.map((row) => {
                const expires = row.check.certificateExpiresAtUtc;
                return (
                  <tr key={row.service.id}>
                    <td>{hostLink(row.host)}</td>
                    <td>{name(row.service)}</td>
                    <td>{row.check.certificateSubject ?? "—"}</td>
                    <td
                      className={
                        daysUntil(expires) <= CERTIFICATE_WARNING_DAYS
                          ? "nk-warning"
                          : undefined
                      }
                    >
                      {dateFormat.format(new Date(expires))} (
                      {expiryText(expires)})
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
