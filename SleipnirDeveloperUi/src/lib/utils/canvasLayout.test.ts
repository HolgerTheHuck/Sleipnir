import { describe, it, expect } from 'vitest';
import {
  NODE_WIDTH,
  HEADER_HEIGHT,
  PORT_ROW_HEIGHT,
  PADDING,
  COL_GAP,
  ROW_GAP,
  NODE_HEIGHT_DEFAULT,
  portAnchor,
  bezierPath,
  assignLevels,
  autoLayout,
} from './canvasLayout';

describe('canvasLayout.portAnchor', () => {
  it('input ports sit on the left edge, output ports on the right edge', () => {
    const node = { x: 100, y: 200 };
    const in0 = portAnchor(node, 'input', 0);
    const out2 = portAnchor(node, 'output', 2);
    expect(in0.x).toBe(100);
    expect(out2.x).toBe(100 + NODE_WIDTH);
    expect(in0.y).toBe(200 + HEADER_HEIGHT + PORT_ROW_HEIGHT / 2);
    expect(out2.y).toBe(200 + HEADER_HEIGHT + 2 * PORT_ROW_HEIGHT + PORT_ROW_HEIGHT / 2);
  });
});

describe('canvasLayout.bezierPath', () => {
  it('produces an SVG cubic-bezier d string from a to b', () => {
    const d = bezierPath({ x: 0, y: 10 }, { x: 200, y: 30 });
    expect(d.startsWith('M 0 10 C ')).toBe(true);
    expect(d.endsWith('200 30')).toBe(true);
  });
});

describe('canvasLayout.assignLevels', () => {
  it('roots are level 0', () => {
    const levels = assignLevels(['a', 'b'], []);
    expect(levels.get('a')).toBe(0);
    expect(levels.get('b')).toBe(0);
  });

  it('linear chain A→B→C gets levels 0,1,2', () => {
    const edges = [
      { from: 'a', to: 'b' },
      { from: 'b', to: 'c' },
    ];
    const levels = assignLevels(['a', 'b', 'c'], edges);
    expect(levels.get('a')).toBe(0);
    expect(levels.get('b')).toBe(1);
    expect(levels.get('c')).toBe(2);
  });

  it('diamond A→B, A→C, B→D, C→D puts D at level 2', () => {
    const edges = [
      { from: 'a', to: 'b' },
      { from: 'a', to: 'c' },
      { from: 'b', to: 'd' },
      { from: 'c', to: 'd' },
    ];
    const levels = assignLevels(['a', 'b', 'c', 'd'], edges);
    expect(levels.get('a')).toBe(0);
    expect(levels.get('b')).toBe(1);
    expect(levels.get('c')).toBe(1);
    expect(levels.get('d')).toBe(2);
  });

  it('cycle does not loop forever (bounded fixpoint)', () => {
    const edges = [
      { from: 'a', to: 'b' },
      { from: 'b', to: 'a' },
    ];
    const levels = assignLevels(['a', 'b'], edges);
    // Both reach the bound without throwing; exact values are not asserted
    // (a cycle has no valid topological level), only termination.
    expect(levels.size).toBe(2);
  });
});

describe('canvasLayout.autoLayout', () => {
  it('lays out columns by level and stacks rows within a column', () => {
    const edges = [{ from: 'a', to: 'b' }];
    const pos = autoLayout(['a', 'b', 'c'], edges);
    // a → level 0, b → level 1, c → level 0 (no edges)
    expect(pos.get('a')!.x).toBe(PADDING);
    expect(pos.get('c')!.x).toBe(PADDING);
    expect(pos.get('b')!.x).toBe(PADDING + (NODE_WIDTH + COL_GAP));
    // a and c share column 0: a at row 0, c at row 1
    expect(pos.get('a')!.y).toBe(PADDING);
    expect(pos.get('c')!.y).toBe(PADDING + (NODE_HEIGHT_DEFAULT + ROW_GAP));
  });
});