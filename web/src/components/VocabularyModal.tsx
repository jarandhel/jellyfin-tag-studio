import { useMemo, useState } from 'preact/hooks';
import type { FieldKind, VocabularyEntry } from '../api';

interface Props {
  entries: VocabularyEntry[];
  field: FieldKind;
  fieldLabel: string;
  busy: boolean;
  onRename: (from: string, to: string) => Promise<void>;
  /** Merge several spellings into one chosen survivor, in a single refresh. */
  onMerge: (from: string[], to: string) => Promise<void>;
  onDelete: (value: string) => Promise<void>;
  onClose: () => void;
}

/**
 * Two entries can only reach the duplicate list by differing in something other than
 * case - the server's vocabulary dictionary is OrdinalIgnoreCase, so case variants
 * were already folded together. What is left is whitespace and Unicode lookalikes,
 * which render identically and leave you picking between "trapped underwater" and
 * "trapped underwater".
 *
 * Spaces become middle dots and anything outside printable ASCII becomes its code
 * point, so a non-breaking space or an en dash is impossible to miss.
 */
const revealed = (name: string) =>
  name.replace(/[^!-~]/g, (c) =>
    c === ' ' ? '·' : 'U+' + c.charCodeAt(0).toString(16).padStart(4, '0').toUpperCase()
  );

/** True when at least two spellings in the group are indistinguishable on screen. */
const looksIdentical = (group: VocabularyEntry[]) => {
  const seen = new Set<string>();
  for (const entry of group) {
    const key = entry.name.replace(/\s+/g, ' ').trim().normalize('NFC').toLowerCase();
    if (seen.has(key)) return true;
    seen.add(key);
  }
  return false;
};

export function VocabularyModal(props: Props) {
  const { entries, fieldLabel, busy, onRename, onMerge, onDelete, onClose } = props;

  const [filter, setFilter] = useState('');
  const [editing, setEditing] = useState<string | null>(null);
  const [draft, setDraft] = useState('');
  const [confirming, setConfirming] = useState<string | null>(null);

  /** Values that differ only by case or punctuation are almost always accidents. */
  const suspicious = useMemo(() => {
    const groups = new Map<string, VocabularyEntry[]>();
    for (const entry of entries) {
      if (!entry.normalizedKey) continue;
      const list = groups.get(entry.normalizedKey) ?? [];
      list.push(entry);
      groups.set(entry.normalizedKey, list);
    }
    return [...groups.values()].filter((g) => g.length > 1);
  }, [entries]);

  const visible = useMemo(() => {
    const term = filter.trim().toLowerCase();
    return entries.filter((e) => !term || e.name.toLowerCase().includes(term));
  }, [entries, filter]);

  const startRename = (entry: VocabularyEntry) => {
    setEditing(entry.name);
    setDraft(entry.name);
    setConfirming(null);
  };

  const commitRename = async (from: string) => {
    const to = draft.trim();
    setEditing(null);
    if (to && to !== from) await onRename(from, to);
  };

  return (
    <div class="ts-modal-backdrop" onClick={onClose}>
      <div class="ts-modal" style="min-width:520px" onClick={(e) => e.stopPropagation()}>
        <h3>Manage {fieldLabel.toLowerCase()}</h3>

        {suspicious.length > 0 && (
          <div class="ts-banner is-warn" style="margin:0">
            <strong>
              {suspicious.length} possible duplicate{suspicious.length === 1 ? '' : 's'}
            </strong>
            <div class="ts-hint" style="margin-top:2px">
              Pick the spelling to keep - the others are merged into it.
            </div>

            <div style="margin-top:8px;display:flex;flex-direction:column;gap:10px">
              {suspicious.slice(0, 8).map((group) => {
                const sorted = [...group].sort((a, b) => b.count - a.count);
                const total = sorted.reduce((n, g) => n + g.count, 0);
                const ambiguous = looksIdentical(sorted);

                return (
                  <div key={sorted[0].normalizedKey} class="ts-dupe-group">
                    <div class="ts-dupe-options">
                      {sorted.map((choice) => (
                        <button
                          key={choice.name}
                          class={`ts-btn ts-dupe-option${ambiguous ? ' is-revealed' : ''}`}
                          disabled={busy}
                          title={`Keep "${choice.name}" and merge the other ${
                            sorted.length - 1
                          } into it`}
                          onClick={() =>
                            onMerge(
                              sorted.filter((v) => v.name !== choice.name).map((v) => v.name),
                              choice.name
                            )
                          }
                        >
                          <span>{ambiguous ? revealed(choice.name) : choice.name}</span>
                          <span class="ts-chip-count">{choice.count.toLocaleString()}</span>
                        </button>
                      ))}
                    </div>
                    <div class="ts-hint">
                      {total.toLocaleString()} items in total
                      {ambiguous && ' · · marks a space, U+XXXX a hidden character'}
                    </div>
                  </div>
                );
              })}
            </div>

            {suspicious.length > 8 && (
              <div class="ts-hint" style="margin-top:6px">
                and {suspicious.length - 8} more - resolve these first
              </div>
            )}
          </div>
        )}

        <input
          class="ts-input"
          placeholder="Filter…"
          value={filter}
          onInput={(e) => setFilter((e.target as HTMLInputElement).value)}
        />

        <div style="max-height:44vh;overflow:auto;border:1px solid var(--ts-border);border-radius:4px">
          {visible.map((entry) => (
            <div
              key={entry.name}
              class="ts-pane-row"
              style="gap:10px;padding:5px 8px;cursor:default"
            >
              {editing === entry.name ? (
                <input
                  class="ts-input"
                  style="flex:1 1 auto"
                  autofocus
                  value={draft}
                  onInput={(e) => setDraft((e.target as HTMLInputElement).value)}
                  onKeyDown={(e) => {
                    if ((e as KeyboardEvent).key === 'Enter') commitRename(entry.name);
                    if ((e as KeyboardEvent).key === 'Escape') setEditing(null);
                  }}
                  onBlur={() => commitRename(entry.name)}
                />
              ) : (
                <>
                  <span class="ts-pane-name" style={entry.isMachine ? 'opacity:.55;font-style:italic' : ''}>
                    {entry.name}
                    {entry.isMachine && ' · auto'}
                  </span>
                  <span class="ts-pane-count">{entry.count.toLocaleString()}</span>
                  <button class="ts-btn" disabled={busy} onClick={() => startRename(entry)}>
                    Rename
                  </button>
                  {confirming === entry.name ? (
                    <button
                      class="ts-btn ts-btn-danger"
                      disabled={busy}
                      onClick={async () => {
                        setConfirming(null);
                        await onDelete(entry.name);
                      }}
                    >
                      Really delete?
                    </button>
                  ) : (
                    <button
                      class="ts-btn ts-btn-danger"
                      disabled={busy || entry.isMachine}
                      title={
                        entry.isMachine
                          ? 'Generated by another plugin — it would come back on the next refresh'
                          : `Remove from all ${entry.count} items`
                      }
                      onClick={() => setConfirming(entry.name)}
                    >
                      Delete
                    </button>
                  )}
                </>
              )}
            </div>
          ))}
          {visible.length === 0 && <div class="ts-empty">Nothing matches.</div>}
        </div>

        <div class="ts-hint">
          Renaming onto a name that already exists merges the two. Every change here is recorded
          and can be undone.
        </div>

        <div class="ts-modal-actions">
          <button class="ts-btn" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
