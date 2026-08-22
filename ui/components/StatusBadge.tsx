export default function StatusBadge({ statusCode }: { statusCode: number | null }) {
  if (statusCode === null) {
    return <span className="status-badge status-network">network error</span>;
  }
  const ok = statusCode >= 200 && statusCode < 300;
  return (
    <span className={`status-badge ${ok ? "status-ok" : "status-fail"}`}>
      {statusCode}
    </span>
  );
}
