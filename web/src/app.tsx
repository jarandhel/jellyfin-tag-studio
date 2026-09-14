import { useCallback, useEffect, useMemo, useRef, useState } from 'preact/hooks';
import {
  api,
  EMPTY_FACETS,
  FIELD_GENRE,
  FIELD_TAG,
  isAuthenticated,
  type CollectionEntry,
  type Facets,
  type FieldKind,
  type ItemRow,
  type VocabularyEntry
} from './api';
import { ColumnBrowser, type PaneSpec } from './components/ColumnBrowser';
import { ItemTable } from './components/ItemTable';
import { TagDock, type ChipModel } from './components/TagDock';
import { VocabularyModal } from './components/VocabularyModal';
import { EmptyCollectionsModal } from './components/EmptyCollectionsModal';

// Sentinels for the pseudo-rows. A literal NUL byte here would make the whole file
// read as binary to git and grep, so it is written as an escape: same value, still
// impossible to collide with a real tag or genre name.
const UNTAGGED = '\u0000untagged';
const NO_GENRE = '\u0000nogenre';

const TYPE_PRESETS: { label: string; types: string[] }[] = [
  { label: 'Movies & Series', types: ['Movie', 'Series'] },
  { label: 'Movies', types: ['Movie'] },
  { label: 'Series', types: ['Series'] },
  { label: 'Seasons', types: ['Season'] },
  { label: 'Episodes', types: ['Episode'] }
];

interface Filters {
  tags: string[];
  genres: string[];
  years: string[];
  studios: string[];
}

const NO_FILTERS: Filters = { tags: [], genres: [], years: [], studios: [] };

export function App() {
  const [typePreset, setTypePreset] = useState(0);
  const [search, setSearch] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [filters, setFilters] = useState<Filters>(NO_FILTERS);
  const [sortBy, setSortBy] = useState('Name');
  const [sortDescending, setSortDescending] = useState(false);

  const [facets, setFacets] = useState<Facets>(EMPTY_FACETS);
  const [reserved, setReserved] = useState<string[]>([]);
  const [items, setItems] = useState<ItemRow[]>([]);
  const [totalCount, setTotalCount] = useState(0);

  const [selected, setSelected] = useState<Set<string>>(new Set());
  // Collections are not a BaseItem field, so they cannot be a FieldKind. The dock
  // still drives them through the same tri-state chips, hence a UI-level mode that
  // maps onto a FieldKind only for tags and genres.
  const [mode, setMode] = useState<'tags' | 'genres' | 'collections'>('tags');
  const field: FieldKind = mode === 'genres' ? FIELD_GENRE : FIELD_TAG;
  const [collections, setCollections] = useState<CollectionEntry[]>([]);
  const [add, setAdd] = useState<Set<string>>(new Set());
  const [remove, setRemove] = useState<Set<string>>(new Set());
  const [propagate, setPropagate] = useState(false);

  const [machineExpanded, setMachineExpanded] = useState(false);
  const [showVocab, setShowVocab] = useState(false);
  const [showEmpty, setShowEmpty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);
  const [streaming, setStreaming] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [lastOperation, setLastOperation] = useState<string | null>(null);

  const searchRef = useRef<HTMLInputElement>(null);
  const requestGeneration = useRef(0);
  const itemTypes = TYPE_PRESETS[typePreset].types;

  /* ---------------- data loading ---------------- */

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(search), 220);
    return () => clearTimeout(timer);
  }, [search]);

  const loadFacets = useCallback(async () => {
    try {
      const [f, r, c] = await Promise.all([
        api.facets(itemTypes),
        api.reservedCharacters(),
        api.collections()
      ]);
      setFacets(f);
      setReserved(r);
      setCollections(c);
    } catch (e) {
      setError(String((e as Error).message ?? e));
    }
  }, [typePreset]);

  /**
   * The server caps a page at MaxQueryPageSize, so a single request only ever
   * returned the first slice of the library. Bulk tagging needs the whole working
   * set selectable, so every page is fetched - but the first one is rendered
   * immediately and the rest appended, so the table is usable straight away.
   */
  const loadItems = useCallback(async () => {
    const generation = ++requestGeneration.current;
    const PAGE_SIZE = 500;

    const base = {
      searchTerm: debouncedSearch || undefined,
      itemTypes,
      tags: filters.tags.filter((t) => t !== UNTAGGED),
      genres: filters.genres.filter((g) => g !== NO_GENRE),
      years: filters.years.map(Number).filter((y) => !Number.isNaN(y)),
      studios: filters.studios,
      untaggedOnly: filters.tags.includes(UNTAGGED),
      noGenreOnly: filters.genres.includes(NO_GENRE),
      sortBy,
      sortDescending
    };

    setLoading(true);
    setStreaming(false);

    try {
      let collected: ItemRow[] = [];

      for (;;) {
        const response = await api.query({
          ...base,
          startIndex: collected.length,
          limit: PAGE_SIZE
        });

        // A newer filter/sort started while this page was in flight.
        if (generation !== requestGeneration.current) return;

        collected = collected.concat(response.items);
        setItems(collected);
        setTotalCount(response.totalCount);
        setError(null);
        setLoading(false);

        const done = response.items.length === 0 || collected.length >= response.totalCount;
        setStreaming(!done);
        if (done) break;
      }
    } catch (e) {
      if (generation !== requestGeneration.current) return;
      setError(String((e as Error).message ?? e));
      setItems([]);
      setTotalCount(0);
    } finally {
      if (generation === requestGeneration.current) {
        setLoading(false);
        setStreaming(false);
      }
    }
  }, [debouncedSearch, typePreset, filters, sortBy, sortDescending]);

  useEffect(() => {
    void loadFacets();
  }, [loadFacets]);

  useEffect(() => {
    void loadItems();
  }, [loadItems]);

  /* ---------------- derived ---------------- */

  const selectedItems = useMemo(
    () => items.filter((i) => selected.has(i.id)),
    [items, selected]
  );

  const fieldLabel =
    mode === 'collections' ? 'Collections' : mode === 'genres' ? 'Genres' : 'Tags';

  const chips = useMemo<ChipModel[]>(() => {
    const counts = new Map<string, number>();
    for (const item of selectedItems) {
      const values =
        mode === 'collections' ? item.collections : mode === 'genres' ? item.genres : item.tags;
      for (const value of values) {
        counts.set(value, (counts.get(value) ?? 0) + 1);
      }
    }

    // Collections another plugin regenerates are locked for the same reason machine
    // tags are: editing one looks like it worked until the generator next runs.
    const machine =
      mode === 'collections'
        ? new Set(collections.filter((c) => c.isManaged).map((c) => c.name))
        : new Set(
            (mode === 'genres' ? facets.genres : facets.tags)
              .filter((v) => v.isMachine)
              .map((v) => v.name)
          );

    return [...counts.entries()]
      .map(([name, present]) => ({ name, present, isMachine: machine.has(name) }))
      .sort(
        (a, b) =>
          Number(a.isMachine) - Number(b.isMachine) ||
          b.present - a.present ||
          a.name.localeCompare(b.name)
      );
  }, [selectedItems, mode, facets, collections]);

  const vocabulary: VocabularyEntry[] =
    mode === 'collections'
      ? collections.map((c) => ({
          name: c.name,
          kind: FIELD_TAG,
          count: c.itemCount,
          isMachine: c.isManaged,
          normalizedKey: ''
        }))
      : mode === 'genres'
        ? facets.genres
        : facets.tags;

  const panes = useMemo<PaneSpec[]>(() => {
    const tagEntries = [
      { name: UNTAGGED, label: '(untagged)', count: facets.untaggedCount, isPseudo: true },
      ...facets.tags.map((t) => ({ name: t.name, count: t.count, isMachine: t.isMachine }))
    ];
    const genreEntries = [
      { name: NO_GENRE, label: '(no genre)', count: facets.noGenreCount, isPseudo: true },
      ...facets.genres.map((g) => ({ name: g.name, count: g.count }))
    ];

    return [
      {
        key: 'tags',
        title: 'Tags',
        entries: tagEntries,
        selected: filters.tags,
        machineGroupLabel: 'Auto-generated',
        machineExpanded,
        onToggleMachineGroup: () => setMachineExpanded((v) => !v)
      },
      { key: 'genres', title: 'Genres', entries: genreEntries, selected: filters.genres },
      {
        key: 'years',
        title: 'Years',
        entries: facets.years.map((y) => ({ name: y.name, count: y.count })),
        selected: filters.years
      },
      {
        key: 'studios',
        title: 'Studios',
        entries: facets.studios.map((s) => ({ name: s.name, count: s.count })),
        selected: filters.studios
      }
    ];
  }, [facets, filters, machineExpanded]);

  const hasFilters =
    filters.tags.length + filters.genres.length + filters.years.length + filters.studios.length > 0;

  /* ---------------- staging ---------------- */

  const clearStaged = () => {
    setAdd(new Set());
    setRemove(new Set());
  };

  const stageAdd = (value: string) => {
    setRemove((prev) => {
      const next = new Set(prev);
      next.delete(value);
      return next;
    });
    setAdd((prev) => new Set(prev).add(value));
  };

  const stageRemove = (value: string) => {
    setAdd((prev) => {
      const next = new Set(prev);
      next.delete(value);
      return next;
    });
    setRemove((prev) => new Set(prev).add(value));
  };

  /** Cycles a chip: partial -> apply to all -> remove from all -> untouched. */
  const toggleChip = (value: string) => {
    if (add.has(value)) {
      setAdd((prev) => {
        const next = new Set(prev);
        next.delete(value);
        return next;
      });
      stageRemove(value);
      return;
    }

    if (remove.has(value)) {
      setRemove((prev) => {
        const next = new Set(prev);
        next.delete(value);
        return next;
      });
      return;
    }

    const chip = chips.find((c) => c.name === value);
    if (chip && chip.present < selected.size) stageAdd(value);
    else stageRemove(value);
  };

  /* ---------------- mutations ---------------- */

  const refreshAfterWrite = async () => {
    clearStaged();
    // loadFacets also reloads the collection list, so a newly created collection
    // appears as a suggestion straight away.
    await Promise.all([loadItems(), loadFacets()]);
  };

  const applyStaged = async () => {
    setBusy(true);
    setError(null);
    try {
      const result =
        mode === 'collections'
          ? await api.applyCollections({
              itemIds: [...selected],
              addTo: [...add],
              removeFrom: [...remove]
            })
          : await api.apply({
              itemIds: [...selected],
              add: [...add],
              remove: [...remove],
              field,
              propagateToChildren: propagate
            });

      if (result.warnings?.length) {
        setError(result.warnings.join(' '));
        return;
      }

      setLastOperation(result.operationId);
      setNotice(
        `Updated ${result.itemsChanged.toLocaleString()} item${result.itemsChanged === 1 ? '' : 's'}` +
          (result.itemsSkipped ? ` · ${result.itemsSkipped} already matched` : '')
      );
      await refreshAfterWrite();
    } catch (e) {
      setError(String((e as Error).message ?? e));
    } finally {
      setBusy(false);
    }
  };

  const runVocabOp = async (fn: () => Promise<{ operationId: string; itemsChanged: number }>) => {
    setBusy(true);
    setError(null);
    try {
      const result = await fn();
      setLastOperation(result.operationId);
      setNotice(`Updated ${result.itemsChanged.toLocaleString()} items`);
      await refreshAfterWrite();
    } catch (e) {
      setError(String((e as Error).message ?? e));
    } finally {
      setBusy(false);
    }
  };

  const undoLast = async () => {
    if (!lastOperation) return;
    setBusy(true);
    try {
      const result = await api.undo(lastOperation);
      setNotice(`Reverted ${result.itemsChanged.toLocaleString()} items`);
      setLastOperation(null);
      await refreshAfterWrite();
    } catch (e) {
      setError(String((e as Error).message ?? e));
    } finally {
      setBusy(false);
    }
  };

  /* ---------------- keyboard ---------------- */

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement;
      const typing = ['INPUT', 'TEXTAREA', 'SELECT'].includes(target?.tagName);

      if (event.key === '/' && !typing) {
        event.preventDefault();
        searchRef.current?.focus();
      } else if ((event.ctrlKey || event.metaKey) && event.key === 'a' && !typing) {
        event.preventDefault();
        setSelected(new Set(items.map((i) => i.id)));
      } else if ((event.ctrlKey || event.metaKey) && event.key === 'z' && !typing) {
        event.preventDefault();
        void undoLast();
      } else if (event.key === 'Escape' && !typing) {
        setSelected(new Set());
        clearStaged();
      }
    };

    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [items, lastOperation]);

  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(null), 4000);
    return () => clearTimeout(timer);
  }, [notice]);

  /* ---------------- render ---------------- */

  if (!isAuthenticated()) {
    return (
      <div class="ts-root">
        <div class="ts-banner is-error">
          Could not find a Jellyfin session. Open Tag Studio from inside the Jellyfin web client
          while signed in as an administrator.
        </div>
      </div>
    );
  }

  return (
    <div class="ts-root">
      <div class="ts-toolbar">
        <input
          ref={searchRef}
          class="ts-search"
          placeholder="Search…  ( / )"
          value={search}
          onInput={(e) => setSearch((e.target as HTMLInputElement).value)}
        />

        <select
          class="ts-select"
          value={String(typePreset)}
          onChange={(e) => {
            setTypePreset(Number((e.target as HTMLSelectElement).value));
            setFilters(NO_FILTERS);
            setSelected(new Set());
            clearStaged();
          }}
        >
          {TYPE_PRESETS.map((p, i) => (
            <option key={p.label} value={String(i)}>
              {p.label}
            </option>
          ))}
        </select>

        <select
          class="ts-select"
          value={mode}
          onChange={(e) => {
            setMode((e.target as HTMLSelectElement).value as typeof mode);
            clearStaged();
          }}
        >
          <option value="tags">Edit tags</option>
          <option value="genres">Edit genres</option>
          <option value="collections">Edit collections</option>
        </select>

        {hasFilters && (
          <button class="ts-btn" onClick={() => setFilters(NO_FILTERS)}>
            Clear filters
          </button>
        )}

        <span class="ts-spacer" />

        <span class="ts-status">
          {loading
            ? 'Loading…'
            : `${items.length.toLocaleString()} of ${totalCount.toLocaleString()}` +
              (streaming ? ' loading…' : '') +
              (selected.size ? ` · ${selected.size.toLocaleString()} selected` : '')}
        </span>

        {mode !== 'collections' ? (
          <button class="ts-btn" onClick={() => setShowVocab(true)} disabled={busy}>
            Manage {fieldLabel.toLowerCase()}…
          </button>
        ) : (
          <button class="ts-btn" onClick={() => setShowEmpty(true)} disabled={busy}>
            Empty collections…
          </button>
        )}
        {/*
          The dashboard resolves a plugin's settings link as
            pages.filter(p => p.PluginId === id).find(p => p.EnableInMainMenu) ?? pages[0]
          and EnableInMainMenu is on this page, so clicking Tag Studio in the plugins list
          lands here rather than on the settings page. That leaves the settings page with
          no route into it from the UI, so link it explicitly.
        */}
        <a class="ts-btn" href="#/configurationpage?name=TagStudioSettings">
          Settings
        </a>
        <button class="ts-btn" onClick={undoLast} disabled={!lastOperation || busy}>
          Undo
        </button>
      </div>

      {error && <div class="ts-banner is-error">{error}</div>}
      {notice && !error && <div class="ts-banner is-warn">{notice}</div>}

      <ColumnBrowser
        panes={panes}
        onChange={(key, values) => {
          setFilters((prev) => ({ ...prev, [key]: values }));
          setSelected(new Set());
          clearStaged();
        }}
      />

      <ItemTable
        items={items}
        selected={selected}
        sortBy={sortBy}
        sortDescending={sortDescending}
        onSort={(key) => {
          if (key === sortBy) setSortDescending((v) => !v);
          else {
            setSortBy(key);
            setSortDescending(false);
          }
        }}
        onSelectionChange={(next) => {
          setSelected(next);
          clearStaged();
        }}
      />

      <TagDock
        selectionSize={selected.size}
        chips={chips}
        vocabulary={vocabulary}
        add={add}
        remove={remove}
        fieldLabel={fieldLabel}
        propagate={propagate}
        showPropagate={mode !== 'collections' && itemTypes.includes('Series')}
        busy={busy}
        reserved={reserved}
        onToggle={toggleChip}
        onStageAdd={stageAdd}
        onStageRemove={stageRemove}
        onPropagateChange={setPropagate}
        onApply={applyStaged}
        onRevert={clearStaged}
      />

      {showVocab && (
        <VocabularyModal
          entries={vocabulary as VocabularyEntry[]}
          field={field}
          fieldLabel={fieldLabel}
          busy={busy}
          onRename={(from, to) => runVocabOp(() => api.rename(from, to, field))}
          onMerge={(sources, target) =>
            runVocabOp(() => api.merge(sources, target, field))
          }
          onDelete={(value) => runVocabOp(() => api.remove(value, field))}
          onClose={() => setShowVocab(false)}
        />
      )}

      {showEmpty && (
        <EmptyCollectionsModal
          onClose={() => setShowEmpty(false)}
          onDeleted={() => void loadFacets()}
        />
      )}
    </div>
  );
}
