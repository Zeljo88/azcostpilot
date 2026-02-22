import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

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
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly baseUrl = 'http://localhost:5168';
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
