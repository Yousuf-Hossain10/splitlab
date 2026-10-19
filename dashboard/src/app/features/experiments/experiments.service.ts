import { Injectable, inject } from '@angular/core';
import { Api } from '../../core/api';
import type { Variation } from '../../api/models';

interface Scope {
  project: string;
  environment: string;
}

@Injectable({ providedIn: 'root' })
export class ExperimentsService {
  private readonly api = inject(Api);

  list({ project, environment }: Scope) {
    return this.api.unwrap(
      this.api.client.GET('/v1/projects/{project}/environments/{environment}/experiments', {
        params: { path: { project, environment } },
      }),
    );
  }

  get({ project, environment }: Scope, key: string) {
    return this.api.unwrap(
      this.api.client.GET('/v1/projects/{project}/environments/{environment}/experiments/{key}', {
        params: { path: { project, environment, key } },
      }),
    );
  }

  create({ project, environment }: Scope, key: string, name: string, variations: Variation[]) {
    return this.api.unwrap(
      this.api.client.POST('/v1/projects/{project}/environments/{environment}/experiments', {
        params: { path: { project, environment } },
        body: { key, name, variations },
      }),
    );
  }

  /** Replaces a draft's variations. `version` is the version the person last saw; a stale one is rejected (409). */
  replaceVariations({ project, environment }: Scope, key: string, version: number, variations: Variation[]) {
    return this.api.unwrap(
      this.api.client.PUT('/v1/projects/{project}/environments/{environment}/experiments/{key}/variations', {
        params: { path: { project, environment, key } },
        body: { version, variations },
      }),
    );
  }

  start({ project, environment }: Scope, key: string, version: number) {
    return this.api.unwrap(
      this.api.client.POST('/v1/projects/{project}/environments/{environment}/experiments/{key}/start', {
        params: { path: { project, environment, key } },
        body: { version },
      }),
    );
  }

  stop({ project, environment }: Scope, key: string, version: number) {
    return this.api.unwrap(
      this.api.client.POST('/v1/projects/{project}/environments/{environment}/experiments/{key}/stop', {
        params: { path: { project, environment, key } },
        body: { version },
      }),
    );
  }

  results({ project, environment }: Scope, key: string, metric: string) {
    return this.api.unwrap(
      this.api.client.GET('/v1/projects/{project}/environments/{environment}/experiments/{key}/results', {
        params: { path: { project, environment, key }, query: { metric } },
      }),
    );
  }
}
