// Pure viewport/coordinate helpers for the dependency query-designer canvas.
// No Svelte, no Sleipnir types — just arithmetic. See canvasGraph.ts for the
// DepStep-aware integration layer.
//
// Two coordinate spaces:
//   - container space: pixels relative to the canvas container's top-left.
//   - canvas space: logical coords in which nodes have x/y (independent of pan/zoom).
// The node layer uses `transform: translate(pan) scale(zoom); transform-origin:0 0`,
// so an element's getBoundingClientRect already reflects pan+zoom. To convert a
// container-space point to canvas space, divide by zoom (pan is already baked in).

export interface Point {
  x: number;
  y: number;
}

/** Container-space point → canvas space (divide by zoom; pan already in the rect). */
export function containerToCanvas(p: Point, zoom: number): Point {
  return { x: p.x / zoom, y: p.y / zoom };
}

/** Canvas space → container space (inverse of containerToCanvas). */
export function canvasToContainer(p: Point, zoom: number): Point {
  return { x: p.x * zoom, y: p.y * zoom };
}

/** Center of a DOM rect (e.g. a port element) in canvas space, given the canvas
 *  container's rect + zoom. The rect already reflects the pan/zoom transform, so
 *  pan is NOT a parameter here. Accepts a minimal rect shape (DOMRect works). */
export function rectCenterToCanvas(
  rect: { left: number; top: number; width: number; height: number },
  containerRect: { left: number; top: number },
  zoom: number,
): Point {
  return containerToCanvas(
    { x: rect.left + rect.width / 2 - containerRect.left, y: rect.top + rect.height / 2 - containerRect.top },
    zoom,
  );
}

/** Pointer event → canvas-space point, given the canvas container rect + zoom. */
export function pointerToCanvas(
  clientX: number,
  clientY: number,
  containerRect: { left: number; top: number },
  zoom: number,
): Point {
  return containerToCanvas({ x: clientX - containerRect.left, y: clientY - containerRect.top }, zoom);
}

/** Clamp zoom to a sane range. */
export function clampZoom(z: number, min = 0.4, max = 2): number {
  return Math.max(min, Math.min(max, z));
}

/** Zoom-about-cursor: adjust pan so the canvas point under the cursor stays fixed.
 *  Returns the new pan. `cursor` is in container space (relative to canvas container). */
export function zoomAboutCursor(
  oldZoom: number,
  newZoom: number,
  cursor: Point,
  oldPan: Point,
): Point {
  // canvas point under cursor (independent of zoom): (cursor - pan) / oldZoom
  // after zoom: cursor = canvasPoint * newZoom + newPan → newPan = cursor - canvasPoint * newZoom
  const canvasPoint = { x: (cursor.x - oldPan.x) / oldZoom, y: (cursor.y - oldPan.y) / oldZoom };
  return { x: cursor.x - canvasPoint.x * newZoom, y: cursor.y - canvasPoint.y * newZoom };
}