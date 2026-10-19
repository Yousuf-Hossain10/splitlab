import { Injectable, inject } from '@angular/core';
import { Api } from '../../core/api';

interface Scope {
  project: string;
  environment: string;
}

@Injectable({ providedIn: 'root' })
export class KeysService {
  private readonly api = inject(Api);

  list({ project, environment }: Scope) {
    return this.api.unwrap(
      this.api.client.GET('/v1/projects/{project}/environments/{environment}/api-keys', {
        params: { path: { project, environment } },
      }),
    );
  }

  create({ project, environment }: Scope, name: string, permissions: string[]) {
    return this.api.unwrap(
      this.api.client.POST('/v1/projects/{project}/environments/{environment}/api-keys', {
        params: { path: { project, environment } },
        body: { name, permissions },
      }),
    );
  }

  async revoke({ project, environment }: Scope, id: string): Promise<void> {
    await this.api.unwrap(
      this.api.client.POST('/v1/projects/{project}/environments/{environment}/api-keys/{id}/revoke', {
        params: { path: { project, environment, id } },
      }),
    );
  }

  rotate({ project, environment }: Scope, id: string) {
    return this.api.unwrap(
      this.api.client.POST('/v1/projects/{project}/environments/{environment}/api-keys/{id}/rotate', {
        params: { path: { project, environment, id } },
      }),
    );
  }
}
