import { render } from 'preact';
import { App } from './app';
import './styles.css';

// Works standalone and when the app is injected into one of Jellyfin's own pages,
// where the container has no useful height of its own.
/**
 * Decide light or dark from the surface we are actually drawn on.
 *
 * prefers-color-scheme reports the OS setting, which says nothing about which
 * Jellyfin theme is active - a light OS under a dark Jellyfin theme gave a white
 * panel with white text, since our text colour defers to --theme-body-text-color.
 * Walking up for the first opaque background answers the real question.
 */
function detectTheme(element: HTMLElement): 'light' | 'dark' {
  for (let node: HTMLElement | null = element; node; node = node.parentElement) {
    const background = getComputedStyle(node).backgroundColor;
    const match = background.match(/rgba?\(([^)]+)\)/);
    if (!match) continue;

    const [r, g, b, a = 1] = match[1].split(',').map((v) => parseFloat(v));
    if (a === 0) continue; // transparent - keep looking upwards

    // Rec. 709 luma; mid-grey and below counts as dark.
    return 0.2126 * r + 0.7152 * g + 0.0722 * b < 128 ? 'dark' : 'light';
  }

  return 'dark';
}

const mount =
  document.getElementById('tag-studio-root') ??
  document.querySelector('.tagStudioMount') ??
  document.body;

/**
 * Jellyfin's dashboard header is fixed and overlays page content. 10.11 renders it
 * with MUI, so the legacy .skinHeader measures zero height - take the tallest
 * top-anchored fixed/sticky bar instead of trusting one selector.
 */
function headerBottom(): number {
  let bottom = 0;
  for (const selector of ['.MuiAppBar-root', '.skinHeader', 'header']) {
    document.querySelectorAll(selector).forEach((node) => {
      const el = node as HTMLElement;
      const position = getComputedStyle(el).position;
      if (position !== 'fixed' && position !== 'sticky') return;
      const rect = el.getBoundingClientRect();
      // Only bars pinned to the top of the viewport can occlude us.
      if (rect.height > 0 && rect.top <= 8) bottom = Math.max(bottom, rect.bottom);
    });
  }
  return bottom;
}

/**
 * Size the app to the space between its own position and the bottom of the viewport,
 * reserving room for the header.
 *
 * Collapsing to zero height first matters: an earlier version measured while the app
 * was already laid out, so pushing it down made the document taller, which scrolled
 * the page, which moved the element back under the header - a loop that cancelled
 * itself out. With the element collapsed the page cannot overflow, so the
 * measurement is stable, and the final height exactly fills the viewport so no
 * scrollbar (and no further reflow) appears.
 *
 * The clearance is padding rather than margin so it counts inside the element's own
 * border-box height instead of adding to the document.
 */
function fitToViewport(element: HTMLElement) {
  const apply = () => {
    element.style.paddingTop = '0px';
    element.style.height = '0px';

    const top = element.getBoundingClientRect().top;
    const clearance = Math.max(0, headerBottom() - top);

    element.style.paddingTop = `${clearance}px`;
    element.style.height = `${Math.max(420, window.innerHeight - top - 4)}px`;
  };

  apply();
  window.addEventListener('resize', apply);
  // Jellyfin animates its page transition; re-measure once it has settled.
  setTimeout(apply, 250);
  setTimeout(apply, 800);
}

const host = mount as HTMLElement;
render(<App />, host);
fitToViewport(host);

const applyTheme = () => {
  host.dataset.tsTheme = detectTheme(host.parentElement ?? host);
};

applyTheme();
// Jellyfin can swap themes without a reload, and the page transition may not have
// painted its background yet on first measure.
setTimeout(applyTheme, 300);
window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', applyTheme);
