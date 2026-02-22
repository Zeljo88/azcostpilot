import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  ApiService,
  DashboardCauseResource,
  DashboardHistoryItem,
  DashboardSummaryResponse,
  DashboardWasteFinding
} from '../services/api.service';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.css'
})
export class DashboardComponent implements OnInit {
  connectionCount = 0;
  loading = true;
  error = '';
  summary: DashboardSummaryResponse | null = null;
  history: DashboardHistoryItem[] = [];
  wasteFindings: DashboardWasteFinding[] = [];

  constructor(private readonly api: ApiService) {}

  async ngOnInit(): Promise<void> {
    const token = this.api.getToken();
    if (!token) {
      this.error = 'No auth token found. Use Connect screen first.';
      return;
    }

    try {
      const [connections, summary, history, wasteFindings] = await Promise.all([
        firstValueFrom(this.api.getAzureConnections()),
        firstValueFrom(this.api.getDashboardSummary()),
        firstValueFrom(this.api.getDashboardHistory()),
        firstValueFrom(this.api.getDashboardWasteFindings())
      ]);
      this.connectionCount = connections.length;
      this.summary = summary;
      this.history = history;
      this.wasteFindings = wasteFindings;
    } catch {
      this.error = 'Could not load dashboard data. Check API and token.';
    } finally {
      this.loading = false;
    }
  }

  formatCurrency(value: number): string {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency: 'USD',
      minimumFractionDigits: 2,
      maximumFractionDigits: 2
    }).format(value ?? 0);
  }

  getTopCause(): DashboardCauseResource | null {
    return this.summary?.topCauseResource ?? null;
  }

  formatFindingType(value: string): string {
    switch (value) {
      case 'unattached_disk':
        return 'Unattached Disk';
      case 'unused_public_ip':
        return 'Unused Public IP';
      case 'stopped_vm':
        return 'Stopped VM';
      default:
        return value.replaceAll('_', ' ');
    }
  }

  buildAzurePortalUrl(resourceId: string | null | undefined): string | null {
    if (!resourceId) {
      return null;
    }

    const normalized = resourceId.startsWith('/') ? resourceId : `/${resourceId}`;
    return `https://portal.azure.com/#resource${normalized}/overview`;
  }
}
