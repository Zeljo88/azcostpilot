import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  ApiService,
  DashboardCauseResource,
  DashboardHistoryItem,
  DashboardSummaryResponse,
  DashboardWasteFinding,
  SeedSyntheticCostDataResponse
} from '../services/api.service';
import { environment } from '../../environments/environment';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.css'
})
export class DashboardComponent implements OnInit {
  readonly showDevScenarios = environment.showDevScenarios;
  readonly devScenarios: ReadonlyArray<{ key: string; label: string; description: string }> = [
    { key: 'normal', label: 'Normal Usage', description: 'Stable daily costs with realistic variance.' },
    {
      key: 'spike',
      label: 'Spike',
      description: 'Sharp increase on the latest complete billing day, usually dominated by one resource.'
    },
    {
      key: 'noisy_increases',
      label: 'Noisy Increases',
      description: 'Multiple resources rise together, lower confidence pattern.'
    },
    { key: 'missing_data', label: 'Missing Data', description: 'Latest day partially missing to simulate lag.' },
    { key: 'idle_resources', label: 'Idle Resources', description: 'Near-zero usage plus waste findings.' }
  ];

  connectionCount = 0;
  loading = true;
  error = '';
  summary: DashboardSummaryResponse | null = null;
  history: DashboardHistoryItem[] = [];
  wasteFindings: DashboardWasteFinding[] = [];
  seedingScenario: string | null = null;
  seedMessage = '';
  seedError = '';

  constructor(private readonly api: ApiService) {}

  async ngOnInit(): Promise<void> {
    const token = this.api.getToken();
    if (!token) {
      this.error = 'No auth token found. Use Connect screen first.';
      this.loading = false;
      return;
    }

    await this.loadDashboardData();
  }

  async seedScenario(scenario: string): Promise<void> {
    this.seedMessage = '';
    this.seedError = '';
    this.seedingScenario = scenario;

    try {
      const result = await firstValueFrom(this.api.seedSyntheticScenario(scenario, 30, true));
      this.seedMessage = this.buildSeedMessage(result);
      await this.refreshDashboardData();
    } catch {
      this.seedError = 'Seeding failed. This endpoint is available only in Development mode.';
    } finally {
      this.seedingScenario = null;
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

  getConfidenceClass(confidence: string | null | undefined): string {
    switch ((confidence ?? '').toLowerCase()) {
      case 'high':
        return 'confidence-high';
      case 'medium':
        return 'confidence-medium';
      default:
        return 'confidence-low';
    }
  }

  getResourceGroup(resourceId: string | null | undefined): string {
    if (!resourceId) {
      return 'Unknown';
    }

    const parts = resourceId.split('/').filter(Boolean);
    const rgIndex = parts.findIndex((part) => part.toLowerCase() === 'resourcegroups');
    if (rgIndex < 0 || rgIndex + 1 >= parts.length) {
      return 'Unknown';
    }

    return parts[rgIndex + 1];
  }

  getFriendlyResourceType(resourceId: string | null | undefined): string {
    if (!resourceId) {
      return 'Unknown';
    }

    const parts = resourceId.split('/').filter(Boolean);
    const providerIndex = parts.findIndex((part) => part.toLowerCase() === 'providers');
    if (providerIndex < 0 || providerIndex + 2 >= parts.length) {
      return 'Unknown';
    }

    const rawType = parts[providerIndex + 2];
    const withSpaces = rawType
      .replace(/([a-z])([A-Z])/g, '$1 $2')
      .replace(/[-_]/g, ' ');
    return withSpaces.charAt(0).toUpperCase() + withSpaces.slice(1);
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

  private async loadDashboardData(): Promise<void> {
    try {
      await this.refreshDashboardData();
    } catch {
      this.error = 'Could not load dashboard data. Check API and token.';
    } finally {
      this.loading = false;
    }
  }

  private async refreshDashboardData(): Promise<void> {
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
  }

  private buildSeedMessage(result: SeedSyntheticCostDataResponse): string {
    return `Seeded "${result.scenario}" (${result.fromDate} to ${result.toDate}), rows: ${result.dailyCostRowsInserted}, events: ${result.eventsGenerated}.`;
  }
}
