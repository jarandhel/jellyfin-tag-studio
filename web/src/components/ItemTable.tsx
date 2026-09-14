import type { ComponentChildren } from 'preact';
import { useEffect, useRef, useState } from 'preact/hooks';
import type { ItemRow } from '../api';

const ROW_HEIGHT = 30;
const OVERSCAN = 12;

export interface Column {
  key: string;
  title: string;
  width: string;
  sortable?: boolean;
  render: (item: ItemRow) => ComponentChildren;
}

interface Props {
  items: ItemRow[];
  selected: Set<string>;
  sortBy: string;
  sortDescending: boolean;
  onSort: (key: string) => void;
  onSelectionChange: (next: Set<string>) => void;
}

export const COLUMNS: Column[] = [
  {
    key: 'Name',
    title: 'Name',
    width: '32%',
    sortable: true,
    render: (i) => (
      <>
        {i.parentName && <span class="ts-parent">{i.parentName} › </span>}
        {episodeCode(i) && <span class="ts-num ts-parent">{episodeCode(i)} </span>}
        {i.name}
      </>
    )
  },
  {
    key: 'ProductionYear',
    title: 'Year',
    width: '62px',
    sortable: true,
    render: (i) => <span class="ts-num">{i.year ?? ''}</span>
  },
  { key: 'Type', title: 'Type', width: '80px', render: (i) => i.type },
  {
    key: 'Genres',
    title: 'Genres',
    width: '20%',
    render: (i) => <span class="ts-cell-tags">{i.genres.join(', ')}</span>
  },
  {
    key: 'Tags',
    title: 'Tags',
    width: 'auto',
    render: (i) => (
      <span class="ts-cell-tags">
        {i.tagsLocked && <span class="ts-lock" title="Tags are locked against metadata refresh">🔒 </span>}
        {i.tags.join(', ')}
      </span>
    )
  },
  {
    key: 'DateCreated',
    title: 'Added',
    width: '96px',
    sortable: true,
    render: (i) => (
      <span class="ts-num">{i.dateCreated ? i.dateCreated.slice(0, 10) : ''}</span>
    )
  }
];

/** S02E05 for episodes, S02 for seasons, blank for anything else. */
export function episodeCode(item: ItemRow): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  if (item.type === 'Episode' && item.indexNumber != null) {
    return item.parentIndexNumber != null
      ? `S${pad(item.parentIndexNumber)}E${pad(item.indexNumber)}`
      : `E${pad(item.indexNumber)}`;
  }
  if (item.type === 'Season' && item.indexNumber != null) return `S${pad(item.indexNumber)}`;
  return '';
}

const HOVER_DELAY_MS = 350;

export function ItemTable({
  items,
  selected,
  sortBy,
  sortDescending,
  onSort,
  onSelectionChange
}: Props) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const anchor = useRef<number>(-1);
  const [range, setRange] = useState({ start: 0, end: 60 });
  const [hover, setHover] = useState<{ item: ItemRow; x: number; y: number } | null>(null);
  const hoverTimer = useRef<number | undefined>(undefined);

  // Delayed so sweeping the pointer down the table does not strobe cards.
  const openCard = (event: MouseEvent, item: ItemRow) => {
    window.clearTimeout(hoverTimer.current);
    if (!item.overview) return;
    const { clientX, clientY } = event;
    hoverTimer.current = window.setTimeout(
      () => setHover({ item, x: clientX, y: clientY }),
      HOVER_DELAY_MS
    );
  };

  const closeCard = () => {
    window.clearTimeout(hoverTimer.current);
    setHover(null);
  };

  useEffect(() => () => window.clearTimeout(hoverTimer.current), []);

  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;

    const recalc = () => {
      const start = Math.max(0, Math.floor(el.scrollTop / ROW_HEIGHT) - OVERSCAN);
      const visible = Math.ceil(el.clientHeight / ROW_HEIGHT) + OVERSCAN * 2;
      setRange({ start, end: Math.min(items.length, start + visible) });
    };

    recalc();
    el.addEventListener('scroll', recalc, { passive: true });
    el.addEventListener('scroll', closeCard, { passive: true });
    const observer = new ResizeObserver(recalc);
    observer.observe(el);
    return () => {
      el.removeEventListener('scroll', recalc);
      el.removeEventListener('scroll', closeCard);
      observer.disconnect();
    };
  }, [items.length]);

  const click = (event: MouseEvent, index: number, item: ItemRow) => {
    const next = new Set(selected);

    if (event.shiftKey && anchor.current >= 0) {
      const [from, to] = [anchor.current, index].sort((a, b) => a - b);
      for (let i = from; i <= to; i++) next.add(items[i].id);
    } else if (event.ctrlKey || event.metaKey) {
      if (next.has(item.id)) next.delete(item.id);
      else next.add(item.id);
      anchor.current = index;
    } else {
      next.clear();
      next.add(item.id);
      anchor.current = index;
    }

    onSelectionChange(next);
  };

  const visible = items.slice(range.start, range.end);
  const above = range.start * ROW_HEIGHT;
  const below = Math.max(0, (items.length - range.end) * ROW_HEIGHT);

  return (
    <div class="ts-table-wrap" ref={scrollRef} tabIndex={0}>
      <table class="ts-table">
        <colgroup>
          {COLUMNS.map((c) => (
            <col key={c.key} style={{ width: c.width }} />
          ))}
        </colgroup>
        <thead>
          <tr>
            {COLUMNS.map((c) => (
              <th
                key={c.key}
                onClick={() => c.sortable && onSort(c.key)}
                style={c.sortable ? undefined : 'cursor:default'}
                title={c.sortable ? `Sort by ${c.title}` : undefined}
              >
                {c.title}
                {sortBy === c.key ? (sortDescending ? ' ▾' : ' ▴') : ''}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {above > 0 && (
            <tr class="ts-spacer-row">
              <td colSpan={COLUMNS.length} style={{ height: `${above}px` }} />
            </tr>
          )}

          {visible.map((item, i) => (
            <tr
              key={item.id}
              class={[
                selected.has(item.id) ? 'is-selected' : '',
                (range.start + i) % 2 === 1 ? 'is-alt' : ''
              ]
                .filter(Boolean)
                .join(' ')}
              onClick={(e) => click(e as MouseEvent, range.start + i, item)}
              onMouseEnter={(e) => openCard(e as MouseEvent, item)}
              onMouseLeave={closeCard}
            >
              {COLUMNS.map((c) => (
                <td key={c.key} title={typeof c.render(item) === 'string' ? String(c.render(item)) : undefined}>
                  {c.render(item)}
                </td>
              ))}
            </tr>
          ))}

          {below > 0 && (
            <tr class="ts-spacer-row">
              <td colSpan={COLUMNS.length} style={{ height: `${below}px` }} />
            </tr>
          )}
        </tbody>
      </table>

      {items.length === 0 && <div class="ts-empty">No items match these filters.</div>}

      {hover && <HoverCard {...hover} />}
    </div>
  );
}

/**
 * Fixed-position so it escapes the table's overflow, and flipped towards whichever
 * side has room so it never runs off the viewport.
 */
function HoverCard({ item, x, y }: { item: ItemRow; x: number; y: number }) {
  const WIDTH = 380;
  const left = x + WIDTH + 24 < window.innerWidth ? x + 16 : Math.max(8, x - WIDTH - 16);
  const flipUp = y + 240 > window.innerHeight;

  const style = flipUp
    ? `left:${left}px; bottom:${Math.max(8, window.innerHeight - y + 16)}px; width:${WIDTH}px`
    : `left:${left}px; top:${y + 16}px; width:${WIDTH}px`;

  const code = episodeCode(item);

  return (
    <div class="ts-hovercard" style={style}>
      <div class="ts-hovercard-title">
        {item.parentName && <span class="ts-parent">{item.parentName} › </span>}
        {code && <span class="ts-parent">{code} </span>}
        {item.name}
      </div>
      <div class="ts-hovercard-meta">
        {[item.type, item.year, item.genres.slice(0, 3).join(', ')].filter(Boolean).join('  ·  ')}
      </div>
      <div class="ts-hovercard-body">{item.overview}</div>
    </div>
  );
}
