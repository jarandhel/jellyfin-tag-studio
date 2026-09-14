export type FieldKind = 0 | 1; // Tag | Genre
export const FIELD_TAG: FieldKind = 0;
export const FIELD_GENRE: FieldKind = 1;

export interface VocabularyEntry {
  name: string;
  kind: FieldKind;
  count: number;
  isMachine: boolean;
  normalizedKey: string;
}

export interface ItemRow {
  id: string;
  name: string;
  type: string;
  year: number | null;
  overview: string;
  parentName: string;
  indexNumber: number | null;
  parentIndexNumber: number | null;
  tags: string[];
  genres: string[];
  studios: string[];
  dateCreated: string;
  tagsLocked: boolean;
  genresLocked: boolean;
}

export interface ItemQueryResponse {
  items: ItemRow[];
  totalCount: number;
}

export interface OperationResult {
  operationId: string;
  itemsChanged: number;
  itemsSkipped: number;
  warnings: string[];
}

export interface HistoryEntry {
  id: string;
  timestamp: string;
  description: string;
  undone: boolean;
}

export interface Facets {
  tags: VocabularyEntry[];
  genres: VocabularyEntry[];
  years: VocabularyEntry[];
  studios: VocabularyEntry[];
  totalItems: number;
  untaggedCount: number;
  noGenreCount: number;
}

export const EMPTY_FACETS: Facets = {
  tags: [],
  genres: [],
  years: [],
  studios: [],
  totalItems: 0,
  untaggedCount: 0,
  noGenreCount: 0
};

/**
 * Tag Studio is served *by* Jellyfin, so the API always lives on this page's own
 * origin. Only the token is borrowed from the existing session - never the address,
 * which can be stale or unreachable from wherever the page happens to be open.
 */
function resolveToken(): string {
  const scopes: any[] = [window];
  try {
    if (window.parent && window.parent !== window) scopes.push(window.parent);
  } catch {
    /* cross-origin parent - ignore */
  }

  for (const scope of scopes) {
    try {
      const token = scope.ApiClient?.accessToken?.();
      if (token) return token;
    } catch {
      /* ignore and try the next scope */
    }
  }

  for (const scope of scopes) {
    try {
      const raw = scope.localStorage?.getItem('jellyfin_credentials');
      if (!raw) continue;
      const server = JSON.parse(raw)?.Servers?.find((s: any) => s.AccessToken);
      if (server?.AccessToken) return server.AccessToken;
    } catch {
      /* ignore and try the next scope */
    }
  }

  return '';
}

const connection = {
  baseUrl: window.location.origin.replace(/\/$/, ''),
  token: resolveToken()
};

const REQUEST_TIMEOUT_MS = 120000;

export const isAuthenticated = () => connection.token !== '';

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  // Without this a stalled request leaves the UI on "Loading..." indefinitely.
  const abort = new AbortController();
  const timer = setTimeout(() => abort.abort(), REQUEST_TIMEOUT_MS);

  let response: Response;
  try {
    response = await fetch(`${connection.baseUrl}/TagStudio${path}`, {
      ...init,
      signal: abort.signal,
      headers: {
        'Content-Type': 'application/json',
        Authorization: `MediaBrowser Token="${connection.token}"`,
        ...(init?.headers ?? {})
      }
    });
  } catch (error) {
    throw new Error(
      (error as Error)?.name === 'AbortError'
        ? `Request to /TagStudio${path} timed out after ${REQUEST_TIMEOUT_MS / 1000}s.`
        : `Could not reach ${connection.baseUrl}/TagStudio${path}: ${(error as Error)?.message}`
    );
  } finally {
    clearTimeout(timer);
  }

  if (!response.ok) {
    const body = await response.text().catch(() => '');
    throw new Error(
      response.status === 401 || response.status === 403
        ? 'Not authorised. Tag Studio requires an administrator account.'
        : `${response.status} ${response.statusText}${body ? ` - ${body}` : ''}`
    );
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

const post = <T>(path: string, body?: unknown) =>
  request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });

/* ---------------------------------------------------------------------------
 * Wire format
 *
 * Jellyfin serialises with PropertyNamingPolicy = null, so everything on the wire
 * is PascalCase and enums arrive as strings ("Tag"/"Genre"). Translating at this
 * boundary keeps the components in ordinary camelCase, and the defaults mean an
 * unexpected shape degrades to an empty list rather than throwing inside render.
 * ------------------------------------------------------------------------- */

const fieldName = (field: FieldKind) => (field === FIELD_GENRE ? 'Genre' : 'Tag');

const asArray = <T>(value: unknown): T[] => (Array.isArray(value) ? (value as T[]) : []);

const mapVocabulary = (v: any): VocabularyEntry => ({
  name: v?.Name ?? '',
  kind: v?.Kind === 'Genre' ? FIELD_GENRE : FIELD_TAG,
  count: v?.Count ?? 0,
  isMachine: !!v?.IsMachine,
  normalizedKey: v?.NormalizedKey ?? ''
});

const mapFacets = (f: any): Facets => ({
  tags: asArray(f?.Tags).map(mapVocabulary),
  genres: asArray(f?.Genres).map(mapVocabulary),
  years: asArray(f?.Years).map(mapVocabulary),
  studios: asArray(f?.Studios).map(mapVocabulary),
  totalItems: f?.TotalItems ?? 0,
  untaggedCount: f?.UntaggedCount ?? 0,
  noGenreCount: f?.NoGenreCount ?? 0
});

const mapItem = (i: any): ItemRow => ({
  id: i?.Id ?? '',
  name: i?.Name ?? '',
  type: i?.Type ?? '',
  year: i?.Year ?? null,
  overview: i?.Overview ?? '',
  parentName: i?.ParentName ?? '',
  indexNumber: i?.IndexNumber ?? null,
  parentIndexNumber: i?.ParentIndexNumber ?? null,
  tags: asArray<string>(i?.Tags),
  genres: asArray<string>(i?.Genres),
  studios: asArray<string>(i?.Studios),
  dateCreated: i?.DateCreated ?? '',
  tagsLocked: !!i?.TagsLocked,
  genresLocked: !!i?.GenresLocked
});

const mapOperation = (o: any): OperationResult => ({
  operationId: o?.OperationId ?? '',
  itemsChanged: o?.ItemsChanged ?? 0,
  itemsSkipped: o?.ItemsSkipped ?? 0,
  warnings: asArray<string>(o?.Warnings)
});

export interface QueryRequest {
  searchTerm?: string;
  itemTypes: string[];
  tags: string[];
  genres: string[];
  years: number[];
  studios: string[];
  untaggedOnly: boolean;
  noGenreOnly: boolean;
  sortBy: string;
  sortDescending: boolean;
  startIndex: number;
  limit: number;
}

export const api = {
  facets: (itemTypes: string[]) =>
    request<any>(
      `/Facets${itemTypes.length ? `?itemTypes=${encodeURIComponent(itemTypes.join(','))}` : ''}`
    ).then(mapFacets),

  reservedCharacters: () => request<any>('/ReservedCharacters').then((r) => asArray<string>(r)),

  query: (body: QueryRequest) =>
    post<any>('/Query', {
      SearchTerm: body.searchTerm,
      ItemTypes: body.itemTypes,
      Tags: body.tags,
      Genres: body.genres,
      Years: body.years,
      Studios: body.studios,
      UntaggedOnly: body.untaggedOnly,
      NoGenreOnly: body.noGenreOnly,
      SortBy: body.sortBy,
      SortDescending: body.sortDescending,
      StartIndex: body.startIndex,
      Limit: body.limit
    }).then(
      (r): ItemQueryResponse => ({
        items: asArray(r?.Items).map(mapItem),
        totalCount: r?.TotalCount ?? 0
      })
    ),

  apply: (body: {
    itemIds: string[];
    add: string[];
    remove: string[];
    field: FieldKind;
    propagateToChildren: boolean;
  }) =>
    post<any>('/Apply', {
      ItemIds: body.itemIds,
      Add: body.add,
      Remove: body.remove,
      Field: fieldName(body.field),
      PropagateToChildren: body.propagateToChildren
    }).then(mapOperation),

  rename: (from: string, to: string, field: FieldKind) =>
    post<any>('/Rename', { From: from, To: to, Field: fieldName(field) }).then(mapOperation),

  /** One journalled operation regardless of how many spellings are collapsed. */
  merge: (from: string[], to: string, field: FieldKind) =>
    post<any>('/Merge', { From: from, To: to, Field: fieldName(field) }).then(mapOperation),

  remove: (value: string, field: FieldKind) =>
    post<any>(`/Delete?value=${encodeURIComponent(value)}&field=${fieldName(field)}`).then(
      mapOperation
    ),

  history: (take = 25) =>
    request<any>(`/History?take=${take}`).then((r) =>
      asArray<any>(r).map(
        (h): HistoryEntry => ({
          id: h?.Id ?? '',
          timestamp: h?.Timestamp ?? '',
          description: h?.Description ?? '',
          undone: !!h?.Undone
        })
      )
    ),

  undo: (operationId: string) => post<any>(`/Undo/${operationId}`).then(mapOperation)
};
