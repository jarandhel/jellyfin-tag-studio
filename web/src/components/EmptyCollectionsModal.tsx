import { useEffect, useState } from 'preact/hooks';
import { api } from '../api';
import type { EmptyCollection, EmptyCollectionReport } from '../api';

interface Props {
  onClose: () => void;
  /** Collections changed on disk, so the caller should reload its list. */
  onDeleted: () => void;
}

/**
 * Empty collections accumulate on their own: Jellyfin's "Clean up collections and
 * playlists" task drops missing items *out of* a collection but leaves the emptied
 * collection behind, so every film that leaves the library can strand one.
 *
 * This is the only destructive action in the plugin with no undo, so the flow is
 * deliberately slower than the rest: nothing is selected implicitly, the blocked
 * entries are shown alongside the deletable ones with their reason, and the button
 * says how many it will remove.
 */
export function EmptyCollectionsModal({ onClose, onDeleted }: Props) {
  const [report, setReport] = useState<EmptyCollectionReport | null>(null);
  const [chosen, setChosen] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [done, setDone] = useState<string[] | null>(null);
  const [confirming, setConfirming] = useState(false);

  const scan = () => {
    setBusy(true);
    setError('');
    api
      .emptyCollections()
      .then((r) => {
        setReport(r);
        // Pre-select every deletable one - that is the common case - but the count on
        // the button always reflects the actual selection rather than the scan.
        setChosen(new Set(r.collections.filter((c) => c.deletable).map((c) => c.id)));
      })
      .catch((e) => setError(String(e?.message ?? e)))
      .finally(() => setBusy(false));
  };

  useEffect(scan, []);

  const toggle = (c: EmptyCollection) => {
    if (!c.deletable) return;
    setChosen((prev) => {
      const next = new Set(prev);
      if (next.has(c.id)) next.delete(c.id);
      else next.add(c.id);
      return next;
    });
  };

  const remove = () => {
    setBusy(true);
    setError('');
    api
      .deleteEmptyCollections([...chosen], false)
      .then((r) => {
        setDone(r.deleted);
        if (r.warnings.length) setError(r.warnings.join('\n'));
        if (r.deleted.length) onDeleted();
        scan();
      })
      .catch((e) => setError(String(e?.message ?? e)))
      .finally(() => {
        setBusy(false);
        setConfirming(false);
      });
  };

  const deletable = report?.collections.filter((c) => c.deletable) ?? [];
  const blocked = report?.collections.filter((c) => !c.deletable) ?? [];

  return (
    <div class="ts-modal-backdrop" onClick={onClose}>
      <div class="ts-modal" style="min-width:560px" onClick={(e) => e.stopPropagation()}>
        <h3>Empty collections</h3>

        <div class="ts-hint" style="margin-bottom:8px">
          Jellyfin's own collection cleanup removes missing items from a collection but
          leaves the empty collection behind. Deleting one cannot be undone.
        </div>

        {error && (
          <div class="ts-banner is-warn" style="margin:0 0 8px">
            {error}
          </div>
        )}

        {done && (
          <div class="ts-banner" style="margin:0 0 8px">
            {done.length === 0
              ? 'Nothing was deleted.'
              : `Deleted ${done.length} collection${done.length === 1 ? '' : 's'}.`}
          </div>
        )}

        {!report && busy && <div class="ts-hint">Scanning…</div>}

        {report && (
          <>
            <div class="ts-hint" style="margin-bottom:6px">
              {report.scanned.toLocaleString()} collections scanned · {deletable.length} empty
              {blocked.length > 0 && ` · ${blocked.length} left alone`}
            </div>

            {deletable.length === 0 && blocked.length === 0 && (
              <div class="ts-hint">No empty collections. Nothing to do.</div>
            )}

            {deletable.length > 0 && (
              <div style="max-height:300px;overflow:auto;margin-bottom:8px">
                {deletable.map((c) => (
                  <label key={c.id} class="ts-pane-row" style="cursor:pointer">
                    <input
                      type="checkbox"
                      checked={chosen.has(c.id)}
                      onChange={() => toggle(c)}
                      disabled={busy}
                    />
                    <span class="ts-pane-name" style="margin-left:6px">
                      {c.name}
                    </span>
                  </label>
                ))}
              </div>
            )}

            {blocked.length > 0 && (
              <details style="margin-bottom:8px">
                <summary class="ts-hint" style="cursor:pointer">
                  {blocked.length} not offered for deletion — why?
                </summary>
                <div style="max-height:180px;overflow:auto;margin-top:4px">
                  {blocked.map((c) => (
                    <div key={c.id} class="ts-pane-row" style="opacity:.7">
                      <span class="ts-pane-name">{c.name}</span>
                      <div class="ts-hint" style="margin-top:2px">
                        {c.blocker}
                      </div>
                    </div>
                  ))}
                </div>
              </details>
            )}
          </>
        )}

        <div class="ts-modal-actions">
          {confirming ? (
            <>
              <span class="ts-hint" style="margin-right:auto">
                Delete {chosen.size} collection{chosen.size === 1 ? '' : 's'}? This cannot be
                undone.
              </span>
              <button class="ts-btn" onClick={() => setConfirming(false)} disabled={busy}>
                Cancel
              </button>
              <button class="ts-btn ts-btn-danger" onClick={remove} disabled={busy}>
                {busy ? 'Deleting…' : 'Delete'}
              </button>
            </>
          ) : (
            <>
              <button class="ts-btn" onClick={scan} disabled={busy} style="margin-right:auto">
                Rescan
              </button>
              <button class="ts-btn" onClick={onClose} disabled={busy}>
                Close
              </button>
              <button
                class="ts-btn ts-btn-danger"
                onClick={() => setConfirming(true)}
                disabled={busy || chosen.size === 0}
              >
                Delete {chosen.size || ''}
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  );
}
