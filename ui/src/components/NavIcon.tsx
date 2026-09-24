// GPD Forge — sidebar icon: the shared Icon set at nav size. GPL-3.0-or-later.
import { Icon } from './Icon'

export type NavIconName =
  'dashboard' | 'power' | 'fan' | 'hardware' | 'display' | 'profiles' |
  'monitor' | 'sessions' | 'system' | 'settings' | 'alerts'

export function NavIcon({ name }: { name: NavIconName }) {
  return <Icon name={name} className="nav-icon" />
}
