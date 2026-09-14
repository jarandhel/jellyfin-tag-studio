import { useEffect, useMemo, useRef, useState } from 'preact/hooks';

const ROW_HEIGHT = 22;
const OVERSCAN = 10;

export interface PaneEntry {
  name: string;
  label?: string;
  count: number;
  isMachine?: boolean;
  isPseudo?: boolean;
}

export interface PaneSpec {
  key: string;
  title: string;
  entries: PaneEntry[];
  selected: string[];
  /** Machine-generated entries are hidden behind a collapsed group. */
  machineGroupLabel?: string;
  machineExpanded?: boolean;
  onToggleMachineGroup?: () => void;
}

interface Props {
  panes: PaneSpec[];
  onChange: (paneKey: string, selected: string[]) => void;
}

/**
 * The iTunes column browser: OR within a pane, AND across panes.
 */
export function ColumnBrowser({ panes, onChange }: Props) {
  return (
    <div class="ts-browser">
      {panes.map((pane) => (
        <Pane key={pane.key} pane={pane} onChange={onChange} />
      ))}
    </div>
  );
}

function Pane({ pane, onChange }: { pane: PaneSpec; onChange: Props['onChange'] }) {
  const lastIndex = useRef<number>(-1);
  const scrollRef = useRef<HTMLDivElement>(null);
  const [filter, setFilter] = useState('');
  const [range, setRange] = useState({ start: 0, end: 60 });

  const visible = useMemo(() => {
    const term = filter.trim().toLowerCase();
    return pane.entries.filter((e) => {
      if (e.isMachine && !pane.machineExpanded && !pane.selected.includes(e.name)) return false;
      if (!term) return true;
      return (e.label ?? e.name).toLowerCase().includes(term);
    });
  }, [pane.entries, pane.machineExpanded, pane.selected, filter]);

  const machineCount = useMemo(
    () => pane.entries.reduce((n, e) => n + (e.isMachine ? 1 : 0), 0),
    [pane.entries]
  );

  // Thousands of tags and studios would otherwise be thousands of DOM rows per pane.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;

    const recalc = () => {
      const start = Math.max(0, Math.floor(el.scrollTop / ROW_HEIGHT) - OVERSCAN);
      const count = Math.ceil(el.clientHeight / ROW_HEIGHT) + OVERSCAN * 2;
      setRange({ start, end: Math.min(visible.length, start + count) });
    };

    recalc();
    el.addEventListener('scroll', recalc, { passive: true });
    const observer = new ResizeObserver(recalc);
    observer.observe(el);
    return () => {
      el.removeEventListener('scroll', recalc);
      observer.disconnect();
    };
  }, [visible.length]);

  const click = (event: MouseEvent, entry: PaneEntry, index: number) => {
    const selected = new Set(pane.selected);

    if (event.shiftKey && lastIndex.current >= 0) {
      const [from, to] = [lastIndex.current, index].sort((a, b) => a - b);
      for (let i = from; i <= to; i++) selected.add(visible[i].name);
    } else if (event.ctrlKey || event.metaKey) {
      if (selected.has(entry.name)) selected.delete(entry.name);
      else selected.add(entry.name);
      lastIndex.current = index;
    } else {
      // Plain click on an already-solitary selection clears it, so a second click
      // on the same row gets you back to "everything".
      const only = pane.selected.length === 1 && pane.selected[0] === entry.name;
      selected.clear();
      if (!only) selected.add(entry.name);
      lastIndex.current = index;
    }

    onChange(pane.key, [...selected]);
  };

  const window_ = visible.slice(range.start, range.end);
  const above = range.start * ROW_HEIGHT;
  const below = Math.max(0, (visible.length - range.end) * ROW_HEIGHT);

  return (
    <div class="ts-pane">
      <div class="ts-pane-head">
        <span>
          {pane.title}
          {pane.selected.length > 0 ? ` (${pane.selected.length})` : ''}
        </span>
        {pane.selected.length > 0 && (
          <button class="ts-pane-clear" onClick={() => onChange(pane.key, [])}>
            clear
          </button>
        )}
      </div>

      <input
        class="ts-pane-filter"
        placeholder={`filter ${visible.length.toLocaleString()}…`}
        value={filter}
        onInput={(e) => {
          setFilter((e.target as HTMLInputElement).value);
          if (scrollRef.current) scrollRef.current.scrollTop = 0;
        }}
      />

      <div class="ts-pane-list" ref={scrollRef}>
        {above > 0 && <div style={{ height: `${above}px` }} />}

        {window_.map((entry, i) => {
          const index = range.start + i;
          const classes = ['ts-pane-row'];
          if (pane.selected.includes(entry.name)) classes.push('is-selected');
          if (entry.isMachine) classes.push('is-machine');
          if (entry.isPseudo) classes.push('is-pseudo');
          if (index % 2 === 1) classes.push('is-alt');

          return (
            <div
              key={entry.name}
              class={classes.join(' ')}
              title={entry.label ?? entry.name}
              onClick={(e) => click(e as MouseEvent, entry, index)}
            >
              <span class="ts-pane-name">{entry.label ?? entry.name}</span>
              <span class="ts-pane-count">{entry.count.toLocaleString()}</span>
            </div>
          );
        })}

        {below > 0 && <div style={{ height: `${below}px` }} />}

        {visible.length === 0 && (
          <div class="ts-empty" style="padding:16px">
            {filter ? 'nothing matches' : 'nothing here'}
          </div>
        )}
      </div>

      {machineCount > 0 && pane.onToggleMachineGroup && (
        <div class="ts-group-toggle" onClick={pane.onToggleMachineGroup}>
          <span>{pane.machineExpanded ? '▾' : '▸'}</span>
          <span>
            {pane.machineGroupLabel ?? 'Auto'} ({machineCount.toLocaleString()})
          </span>
        </div>
      )}
    </div>
  );
}
