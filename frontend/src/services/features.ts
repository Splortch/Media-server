import { api } from './api';
import type { components } from '../../../shared/api/src/generated';
export type Feature = Omit<
  components['schemas']['FeatureCapability'],
  'key'
> & {
  key: 'smart-collections' | 'watch-history';
};
export type FeatureState = { features: Feature[]; available: boolean };
export const unavailableFeatures: FeatureState = {
  features: [],
  available: false,
};
export function featureVisible(state: FeatureState, key: Feature['key']) {
  return state.features.some(
    (f) => f.key === key && f.enabled && f.visible && !f.hidden,
  );
}
export function parseFeatures(value: unknown): FeatureState {
  if (
    !value ||
    typeof value !== 'object' ||
    !('features' in value) ||
    !Array.isArray(value.features)
  )
    throw new Error('Unsupported feature response.');
  const features: Feature[] = value.features.filter(
    (f): f is Feature =>
      f &&
      ['smart-collections', 'watch-history'].includes(f.key) &&
      typeof f.name === 'string' &&
      f.name.length <= 100 &&
      ['enabled', 'visible', 'hidden', 'serverConfigurable'].every(
        (k) => typeof f[k] === 'boolean',
      ),
  );
  return { features, available: true };
}
export async function loadFeatures(): Promise<FeatureState> {
  return parseFeatures(await api('/features'));
}
