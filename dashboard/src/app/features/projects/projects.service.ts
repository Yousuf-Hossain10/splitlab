import { Injectable, inject } from '@angular/core';
import { Api } from '../../core/api';

@Injectable({ providedIn: 'root' })
export class ProjectsService {
  private readonly api = inject(Api);

  list() {
    return this.api.unwrap(this.api.client.GET('/v1/projects'));
  }

  create(key: string, name: string) {
    return this.api.unwrap(this.api.client.POST('/v1/projects', { body: { key, name } }));
  }

  get(project: string) {
    return this.api.unwrap(this.api.client.GET('/v1/projects/{project}', { params: { path: { project } } }));
  }

  environments(project: string) {
    return this.api.unwrap(
      this.api.client.GET('/v1/projects/{project}/environments', { params: { path: { project } } }),
    );
  }

  createEnvironment(project: string, key: string, name: string) {
    return this.api.unwrap(
      this.api.client.POST('/v1/projects/{project}/environments', {
        params: { path: { project } },
        body: { key, name },
      }),
    );
  }
}
