import { describe, it, expect } from 'vitest';
import {
  containerToCanvas,
  canvasToContainer,
  rectCenterToCanvas,
  pointerToCanvas,
  clampZoom,
  zoomAboutCursor,
} from './canvasViewport';

describe('canvasViewport', () => {
  it('containerToCanvas / canvasToContainer round-trip', () => {
    const p = { x: 123.4, y: -7 };
    for (const zoom of [1, 1.5, 0.5, 2]) {
      expect(canvasToContainer(containerToCanvas(p, zoom), zoom)).toEqual(p);
    }
  });

  it('containerToCanvas divides by zoom', () => {
    expect(containerToCanvas({ x: 100, y: 50 }, 2)).toEqual({ x: 50, y: 25 });
  });

  it('rectCenterToCanvas converts a port rect center to canvas space', () => {
    const rect = { left: 200, top: 80, width: 20, height: 20 };
    const container = { left: 40, top: 20 };
    // rect center in viewport: (210, 90); minus container origin → (170, 70); /2 zoom → (85, 35)
    expect(rectCenterToCanvas(rect, container, 2)).toEqual({ x: 85, y: 35 });
  });

  it('pointerToCanvas subtracts container origin then divides by zoom', () => {
    expect(pointerToCanvas(140, 70, { left: 40, top: 20 }, 2)).toEqual({ x: 50, y: 25 });
  });

  it('clampZoom clamps to range', () => {
    expect(clampZoom(3)).toBe(2);
    expect(clampZoom(0.1)).toBe(0.4);
    expect(clampZoom(1)).toBe(1);
  });

  it('zoomAboutCursor keeps the canvas point under the cursor fixed', () => {
    const cursor = { x: 300, y: 200 };
    const oldPan = { x: 50, y: 30 };
    const oldZoom = 1;
    const newZoom = 2;
    const newPan = zoomAboutCursor(oldZoom, newZoom, cursor, oldPan);
    // canvas point under cursor before: (300-50)/1 = 250, (200-30)/1 = 170
    // after: cursor = canvasPoint*newZoom + newPan → newPan = cursor - canvasPoint*newZoom
    expect(newPan).toEqual({ x: 300 - 250 * 2, y: 200 - 170 * 2 });
    // the same canvas point maps back to the cursor at the new zoom:
    const back = { x: 250 * newZoom + newPan.x, y: 170 * newZoom + newPan.y };
    expect(back).toEqual(cursor);
  });
});