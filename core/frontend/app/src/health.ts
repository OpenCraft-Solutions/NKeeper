/** Describing health results in the browser (docs/architecture.md §7.13, §7.14). */

/** A certificate this close to expiring is marked. */
export const CERTIFICATE_WARNING_DAYS = 30;

/** Whole days from now until `utc`; negative once it's past. */
export function daysUntil(utc: string, now = Date.now()): number {
  return Math.floor((new Date(utc).getTime() - now) / 86_400_000);
}

/** "in 12 days", or "expired 3 days ago". */
export function expiryText(utc: string, now = Date.now()): string {
  const days = daysUntil(utc, now);
  return days < 0
    ? `expired ${-days} ${days === -1 ? "day" : "days"} ago`
    : `in ${days} ${days === 1 ? "day" : "days"}`;
}
