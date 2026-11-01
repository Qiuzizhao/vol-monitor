export interface Session { id: string; name: string; mode: string; demo: boolean; status: string; startedAt: string; endedAt: string | null; algorithm?: string }
export interface Seat { id: string; label: string; row: number; column: number; zoneId: string; deviceId: string | null; deviceName: string | null; status: string; score: number | null; levelDbfs: number | null; baselineDbfs: number | null; lastSeen: string | null; clippingRatio: number | null }
export interface Zone { id: string; name: string; score: number | null; valid: number; expected: number; limitedCoverage: boolean; alert: boolean }
export interface Device { id: string; name: string; seatId: string | null; status: string; microphoneId: string | null; baselineDbfs: number | null; lastSeen: string | null }
export interface Trend { time: string; average: number | null; zones: Record<string, number | null> }
export interface Annotation { id: string; sessionId: string; time: string; text: string; kind: string }
export interface Dashboard { layoutRows: number; layoutColumns: number; roomName: string; serverTime: string; algorithmVersion: string; session: Session | null; calibrationId: string | null; calibrationUntil: string | null; seats: Seat[]; zones: Zone[]; devices: Device[]; trend: Trend[]; annotations: Annotation[] }
