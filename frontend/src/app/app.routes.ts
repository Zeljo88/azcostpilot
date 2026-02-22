import { Routes } from '@angular/router';
import { ConnectComponent } from './pages/connect.component';
import { DashboardComponent } from './pages/dashboard.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'connect' },
  { path: 'connect', component: ConnectComponent },
  { path: 'dashboard', component: DashboardComponent },
  { path: '**', redirectTo: 'connect' }
];
