import type { components } from './schema';

// Friendly names for the generated API types. The shapes come from the OpenAPI document (ADR-0008); never hand-write them.
type Schemas = components['schemas'];

export type Project = Schemas['ProjectResponse'];
export type Environment = Schemas['EnvironmentResponse'];
export type Experiment = Schemas['ExperimentResponse'];
export type Variation = Schemas['VariationDto'];
export type ApiKey = Schemas['ApiKeyResponse'];
export type IssuedApiKey = Schemas['IssuedApiKeyResponse'];
export type ResultsResponse = Schemas['ResultsResponse'];
export type ExperimentResults = Schemas['ExperimentResults'];
export type ExperimentAnalysis = Schemas['ExperimentAnalysis'];
export type VariationResult = Schemas['VariationResult'];
export type Recommendation = Schemas['Recommendation'];
export type RecommendationKind = Schemas['RecommendationKind'];
export type SampleRatio = NonNullable<Schemas['SampleRatioMismatchResult']>;
export type ProblemDetails = Schemas['ProblemDetails'];
