import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../services/api.service';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.css'
})
export class DashboardComponent implements OnInit {
  connectionCount = 0;
  error = '';

  constructor(private readonly api: ApiService) {}

  async ngOnInit(): Promise<void> {
    const token = this.api.getToken();
    if (!token) {
      this.error = 'No auth token found. Use Connect screen first.';
      return;
    }

    try {
      const connections = await firstValueFrom(this.api.getAzureConnections());
      this.connectionCount = connections.length;
    } catch {
      this.error = 'Could not load dashboard data. Check API and token.';
    }
  }
}
