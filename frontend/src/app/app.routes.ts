import { Routes } from '@angular/router';
import { ConnectComponent } from './pages/connect.component';
import { DashboardComponent } from './pages/dashboard.component';
import { LandingComponent } from './pages/landing.component';

export const routes: Routes = [
  { path: '', component: LandingComponent },
  { path: 'connect', component: ConnectComponent },
  { path: 'dashboard', component: DashboardComponent },
  { path: '**', redirectTo: '' }
];
