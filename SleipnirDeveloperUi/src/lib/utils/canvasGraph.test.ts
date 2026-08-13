import { describe, it, expect } from 'vitest';
import type { DiscoveryInfo } from 'sleipnir-client';
import {
  aliasEdges,
  deriveEdges,
  commitEdge,
  removeEdge,
  generateAlias,
  aliasBaseFromPath,
  nextDefaultStepId,
  autoLayoutSteps,
  ensurePositions,
} from './canvasGraph';
// Type-only import from the runes state module: erased by esbuild, so vitest
// (no Svelte plugin) never loads tabs.svelte.ts and never sees `$state`.
import type { DepStep as Step } from '../state/tabs.svelte';

// --- Discovery fixture (minimal, schema-correct) ----------------------------

const orderType = {
  kind: 'object' as const,
  typeName: 'Demo.Order',
  properties: [
    { propertyName: 'Id', propertyType: { kind: 'scalar' as const, name: 'int' } },
    { propertyName: 'CustomerId', propertyType: { kind: 'scalar' as const, name: 'string' } },
  ],
};

const discovery: DiscoveryInfo = {
  discoveryVersion: '1',
  controllers: [
    {
      name: 'OrderCtl',
      methods: [
        { methodName: 'getOrder', returnType: { kind: 'ref', ref: 'Demo.Order' }, parameters: [] },
        {
          methodName: 'save',
          returnType: { kind: 'void' },
          parameters: [{ parameterName: 'customerId', parameterType: { kind: 'scalar', name: 'string' } }],
        },
        {
          methodName: 'saveCount',
          returnType: { kind: 'void' },
          parameters: [{ parameterName: 'count', parameterType: { kind: 'scalar', name: 'int' } }],
        },
      ],
    },
  ],
  types: { 'Demo.Order': orderType },
};

// --- Step builders ----------------------------------------------------------

function step(id: string, overrides: Partial<Step> = {}): Step {
  return { id, controller: '', method: '', params: [], exposes: [], ...overrides };
}

function param(parameterName: string, scalar: string, useAlias = false, aliasRef?: string): Step['params'][number] {
  return {
    parameterName,
    parameterType: { kind: 'scalar', name: scalar },
    useAlias,
    aliasRef,
    literalValue: '',
  };
}

describe('canvasGraph.aliasEdges', () => {
  it('builds provider→consumer edges honoring Serial order', () => {
    const steps: Step[] = [
      step('a', { exposes: [{ alias: 'cid', jsonPath: '$.CustomerId' }] }),
      step('b', { params: [param('customerId', 'string', true, 'cid')] }),
    ];
    expect(aliasEdges(steps)).toEqual([{ from: 'a', to: 'b' }]);
  });

  it('does NOT bind a consumer to an alias exposed by a LATER step', () => {
    const steps: Step[] = [
      step('b', { params: [param('customerId', 'string', true, 'cid')] }),
      step('a', { exposes: [{ alias: 'cid', jsonPath: '$.CustomerId' }] }),
    ];
    expect(aliasEdges(steps)).toEqual([]);
  });

  it('diamond: one provider feeds two consumers', () => {
    const steps: Step[] = [
      step('a', { exposes: [{ alias: 'cid', jsonPath: '$.CustomerId' }] }),
      step('b', { params: [param('customerId', 'string', true, 'cid')] }),
      step('c', { params: [param('customerId', 'string', true, 'cid')] }),
    ];
    expect(aliasEdges(steps).sort((x, y) => x.to.localeCompare(y.to))).toEqual([
      { from: 'a', to: 'b' },
      { from: 'a', to: 'c' },
    ]);
  });
});

describe('canvasGraph.deriveEdges', () => {
  it('produces an ok edge for a compatible string→string binding', () => {
    const steps: Step[] = [
      step('a', { controller: 'OrderCtl', method: 'getOrder', exposes: [{ alias: 'cid', jsonPath: '$.customerId' }] }),
      step('b', { controller: 'OrderCtl', method: 'save', params: [param('customerId', 'string', true, 'cid')] }),
    ];
    const edges = deriveEdges(steps, discovery);
    expect(edges).toHaveLength(1);
    expect(edges[0].severity).toBe('ok');
    expect(edges[0].fromStepId).toBe('a');
    expect(edges[0].toStepId).toBe('b');
    expect(edges[0].alias).toBe('cid');
    expect(edges[0].fromPortIndex).toBe(0);
    expect(edges[0].toPortIndex).toBe(0);
  });

  it('produces an error edge for a cross-kind string→number binding', () => {
    const steps: Step[] = [
      step('a', { controller: 'OrderCtl', method: 'getOrder', exposes: [{ alias: 'cid', jsonPath: '$.customerId' }] }),
      step('c', { controller: 'OrderCtl', method: 'saveCount', params: [param('count', 'int', true, 'cid')] }),
    ];
    const edges = deriveEdges(steps, discovery);
    expect(edges).toHaveLength(1);
    expect(edges[0].severity).toBe('error');
    expect(edges[0].message).toMatch(/string/i);
  });

  it('flags a broken expose path (JsonPath matches nothing) as error', () => {
    const steps: Step[] = [
      step('a', { controller: 'OrderCtl', method: 'getOrder', exposes: [{ alias: 'cid', jsonPath: '$.nope' }] }),
      step('b', { controller: 'OrderCtl', method: 'save', params: [param('customerId', 'string', true, 'cid')] }),
    ];
    const edges = deriveEdges(steps, discovery);
    expect(edges[0].severity).toBe('error');
  });

  it('with null discovery, structural edges still form with ok severity', () => {
    const steps: Step[] = [
      step('a', { exposes: [{ alias: 'cid', jsonPath: '$.CustomerId' }] }),
      step('b', { params: [param('customerId', 'string', true, 'cid')] }),
    ];
    const edges = deriveEdges(steps, null);
    expect(edges).toHaveLength(1);
    expect(edges[0].severity).toBe('ok');
  });
});

describe('canvasGraph.commitEdge', () => {
  it('adds an expose to the provider and flips the consumer param to alias', () => {
    const steps: Step[] = [
      step('a', { controller: 'OrderCtl', method: 'getOrder' }),
      step('b', { controller: 'OrderCtl', method: 'save', params: [param('customerId', 'string')] }),
    ];
    const next = commitEdge(steps, 'a', '$.customerId', 'b', 'customerId', 'cid');
    expect(next[0].exposes).toEqual([{ alias: 'cid', jsonPath: '$.customerId' }]);
    expect(next[1].params[0].useAlias).toBe(true);
    expect(next[1].params[0].aliasRef).toBe('cid');
  });

  it('is idempotent: committing the same alias twice does not duplicate the expose', () => {
    const steps: Step[] = [
      step('a', { exposes: [{ alias: 'cid', jsonPath: '$.customerId' }] }),
      step('b', { params: [param('customerId', 'string')] }),
    ];
    const next = commitEdge(steps, 'a', '$.customerId', 'b', 'customerId', 'cid');
    expect(next[0].exposes).toHaveLength(1);
  });

  it('does not mutate the input array (purity / structural sharing)', () => {
    const steps: Step[] = [
      step('a', { controller: 'OrderCtl', method: 'getOrder' }),
      step('b', { controller: 'OrderCtl', method: 'save', params: [param('customerId', 'string')] }),
    ];
    const next = commitEdge(steps, 'a', '$.customerId', 'b', 'customerId', 'cid');
    expect(steps[0].exposes).toEqual([]); // original untouched
    expect(next[1]).not.toBe(steps[1]); // changed step is a new object
    expect(next[0]).not.toBe(steps[0]); // changed step is a new object
  });
});

describe('canvasGraph.removeEdge', () => {
  it('unbinds the consumer param and drops the expose when no other consumer uses it', () => {
    const steps: Step[] = [
      step('a', { exposes: [{ alias: 'cid', jsonPath: '$.customerId' }] }),
      step('b', { params: [param('customerId', 'string', true, 'cid')] }),
    ];
    const next = removeEdge(steps, { fromStepId: 'a', toStepId: 'b', alias: 'cid', paramName: 'customerId' });
    expect(next[1].params[0].useAlias).toBe(false);
    expect(next[1].params[0].aliasRef).toBeUndefined();
    expect(next[0].exposes).toEqual([]);
  });

  it('keeps the expose when another consumer still binds the alias', () => {
    const steps: Step[] = [
      step('a', { exposes: [{ alias: 'cid', jsonPath: '$.customerId' }] }),
      step('b', { params: [param('customerId', 'string', true, 'cid')] }),
      step('c', { params: [param('customerId', 'string', true, 'cid')] }),
    ];
    const next = removeEdge(steps, { fromStepId: 'a', toStepId: 'b', alias: 'cid', paramName: 'customerId' });
    expect(next[0].exposes).toEqual([{ alias: 'cid', jsonPath: '$.customerId' }]);
    expect(next[1].params[0].useAlias).toBe(false);
    expect(next[2].params[0].useAlias).toBe(true);
  });
});

describe('canvasGraph.aliasBaseFromPath + generateAlias', () => {
  it('extracts the last path segment', () => {
    expect(aliasBaseFromPath('$.order.customerId')).toBe('customerId');
    expect(aliasBaseFromPath('$[0].id')).toBe('id');
    expect(aliasBaseFromPath('$')).toBe('');
    expect(aliasBaseFromPath('$[0]')).toBe('');
    expect(aliasBaseFromPath('$.a.b.c')).toBe('c');
  });

  it('prefers a path-derived base, then numeric suffix, then a1 fallback', () => {
    expect(generateAlias([], '$.order.customerId')).toBe('customerId');
    const taken: Step[] = [step('a', { exposes: [{ alias: 'customerId', jsonPath: '$' }] })];
    expect(generateAlias(taken, '$.order.customerId')).toBe('customerId2');
    expect(generateAlias([step('a', { exposes: [{ alias: 'customerId', jsonPath: '$' }, { alias: 'customerId2', jsonPath: '$' }] })], '$.order.customerId')).toBe('customerId3');
    expect(generateAlias([], '$')).toBe('a1');
    expect(generateAlias([step('a', { exposes: [{ alias: 'a1', jsonPath: '$' }] })], '$')).toBe('a2');
  });
});

describe('canvasGraph.nextDefaultStepId', () => {
  it('produces stepN avoiding collisions', () => {
    expect(nextDefaultStepId([])).toBe('step1');
    expect(nextDefaultStepId([step('step1')])).toBe('step2');
    expect(nextDefaultStepId([step('step1'), step('step3')])).toBe('step2');
  });
});

describe('canvasGraph.autoLayoutSteps + ensurePositions', () => {
  it('autoLayoutSteps places a chain left-to-right by level', () => {
    const steps: Step[] = [
      step('a', { exposes: [{ alias: 'cid', jsonPath: '$.CustomerId' }] }),
      step('b', { params: [param('customerId', 'string', true, 'cid')] }),
    ];
    const pos = autoLayoutSteps(steps);
    expect(pos.get('a')!.x).toBeLessThan(pos.get('b')!.x);
  });

  it('ensurePositions keeps explicit positions and fills the rest', () => {
    const steps: Step[] = [
      step('a', { x: 500, y: 600, exposes: [{ alias: 'cid', jsonPath: '$.CustomerId' }] }),
      step('b', { params: [param('customerId', 'string', true, 'cid')] }),
    ];
    const pos = ensurePositions(steps);
    expect(pos.get('a')).toEqual({ x: 500, y: 600 }); // kept
    expect(pos.get('b')!.x).toBeGreaterThan(0); // filled by autoLayout
  });
});