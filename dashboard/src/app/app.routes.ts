import { Routes } from '@angular/router';
import { authGuard } from './core/auth.guard';

// Feature folders are lazy-loaded (ADR-0008); the shell and guard are all the first load needs.
export const routes: Routes = [
  {
    path: 'sign-in',
    title: 'Sign in · Splitlab',
    loadComponent: () => import('./features/sign-in/sign-in.page').then((m) => m.SignInPage),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./shared/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'projects' },
      {
        path: 'projects',
        title: 'Projects · Splitlab',
        loadComponent: () => import('./features/projects/projects.page').then((m) => m.ProjectsPage),
      },
      {
        path: 'projects/:project',
        title: 'Project · Splitlab',
        loadComponent: () => import('./features/projects/project.page').then((m) => m.ProjectPage),
      },
      {
        path: 'projects/:project/environments/:environment',
        title: 'Experiments · Splitlab',
        loadComponent: () => import('./features/experiments/experiments.page').then((m) => m.ExperimentsPage),
      },
      {
        path: 'projects/:project/environments/:environment/keys',
        title: 'SDK keys · Splitlab',
        loadComponent: () => import('./features/settings/keys.page').then((m) => m.KeysPage),
      },
      {
        path: 'projects/:project/environments/:environment/experiments/:key',
        title: 'Experiment · Splitlab',
        loadComponent: () => import('./features/experiments/experiment.page').then((m) => m.ExperimentPage),
      },
      {
        path: 'projects/:project/environments/:environment/experiments/:key/results',
        title: 'Results · Splitlab',
        loadComponent: () => import('./features/results/results.page').then((m) => m.ResultsPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
