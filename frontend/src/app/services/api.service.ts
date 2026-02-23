import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface AuthResponse {
  token: string;
  email: string;
  userId: string;
}

export interface AzureConnectionSummary {
  id: string;
  tenantId: string;
  clientId: string;
  createdAtUtc: string;
}

export interface ConnectedSubscription {
  subscriptionId: string;
  displayName: string;
  state: string;
}

export interface ConnectAzureResponse {
  connected: boolean;
  connectionId: string;
  subscriptionCount: number;
  subscriptions: ConnectedSubscription[];
  backfillCompleted: boolean;
  backfillMessage: string;
}

export interface DashboardCauseResource {
  resourceId: string;
  resourceName: string;
  resourceType: string;
  increaseAmount: number;
}

export interface DashboardSummaryResponse {
  date: string;
  latestDataDate: string;
  yesterdayTotal: number;
  todayTotal: number;
  difference: number;
  baseline: number;
  monthToDateTotal: number;
  spikeFlag: boolean;
  confidence: 'High' | 'Medium' | 'Low' | string;
  topCauseResource: DashboardCauseResource | null;
  suggestionText: string;
}

export interface DashboardHistoryItem {
  date: string;
  yesterdayTotal: number;
  todayTotal: number;
  difference: number;
  spikeFlag: boolean;
  topResourceName: string | null;
  topIncreaseAmount: number | null;
}

export interface DashboardWasteFinding {
  findingType: string;
  resourceId: string;
  resourceName: string;
  azureSubscriptionId: string;
  estimatedMonthlyCost: number | null;
  detectedAtUtc: string;
  status: string;
}

export interface SeedSyntheticCostDataResponse {
  scenario: string;
  days: number;
  dailyCostRowsInserted: number;
  wasteFindingsInserted: number;
  eventsGenerated: number;
  fromDate: string;
  toDate: string;
  note: string;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly baseUrl = environment.apiBaseUrl;
  private readonly tokenStorageKey = 'azcostpilot_token';

  constructor(private readonly http: HttpClient) {}

  register(email: string, password: string): Observable<unknown> {
    return this.http.post(`${this.baseUrl}/auth/register`, { email, password });
  }

  login(email: string, password: string): Observable<AuthResponse> {
    return this.http.post<AuthResponse>(`${this.baseUrl}/auth/login`, { email, password });
  }

  saveAzureConnection(tenantId: string, clientId: string, clientSecret: string): Observable<ConnectAzureResponse> {
    return this.http.post<ConnectAzureResponse>(
      `${this.baseUrl}/connect/azure`,
      { tenantId, clientId, clientSecret },
      { headers: this.authHeaders() }
    );
  }

  getAzureConnections(): Observable<AzureConnectionSummary[]> {
    return this.http.get<AzureConnectionSummary[]>(`${this.baseUrl}/connections/azure`, {
      headers: this.authHeaders()
    });
  }

  getDashboardSummary(): Observable<DashboardSummaryResponse> {
    return this.http.get<DashboardSummaryResponse>(`${this.baseUrl}/dashboard/summary`, {
      headers: this.authHeaders()
    });
  }

  getDashboardHistory(threshold = 5): Observable<DashboardHistoryItem[]> {
    return this.http.get<DashboardHistoryItem[]>(`${this.baseUrl}/dashboard/history?threshold=${threshold}`, {
      headers: this.authHeaders()
    });
  }

  getDashboardWasteFindings(): Observable<DashboardWasteFinding[]> {
    return this.http.get<DashboardWasteFinding[]>(`${this.baseUrl}/dashboard/waste-findings`, {
      headers: this.authHeaders()
    });
  }

  seedSyntheticScenario(
    scenario: string,
    days = 30,
    clearExistingData = true,
    seed?: number
  ): Observable<SeedSyntheticCostDataResponse> {
    const body: { scenario: string; days: number; clearExistingData: boolean; seed?: number } = {
      scenario,
      days,
      clearExistingData
    };

    if (seed !== undefined) {
      body.seed = seed;
    }

    return this.http.post<SeedSyntheticCostDataResponse>(`${this.baseUrl}/dev/seed/cost-scenarios`, body, {
      headers: this.authHeaders()
    });
  }

  setToken(token: string): void {
    localStorage.setItem(this.tokenStorageKey, token);
  }

  getToken(): string | null {
    return localStorage.getItem(this.tokenStorageKey);
  }

  private authHeaders(): HttpHeaders {
    const token = this.getToken();
    return new HttpHeaders(token ? { Authorization: `Bearer ${token}` } : {});
  }
}
