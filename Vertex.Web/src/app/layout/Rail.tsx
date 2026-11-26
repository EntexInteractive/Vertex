import {NavLink} from 'react-router'
import {Avatar} from '../../components/Avatar'
import {Icon, type IconName} from '../../components/Icon'
import {useSession} from '../session'
import {useWorkspace} from '../workspace'

type Section = {path: string; icon: IconName; label: string; badge?: (counts: {changes: number}) => number}

// One entry per area that works today. New areas (builds, issues, reviews) get their own entry here; actions that
// don't need a page go in the command palette instead.
const sections: ReadonlyArray<Section> = [
    {path: '/changes', icon: 'code', label: 'Changes', badge: c => c.changes},
    {path: '/history', icon: 'history', label: 'History'},
    {path: '/branches', icon: 'branch', label: 'Branches'},
    {path: '/locks', icon: 'lock', label: 'Locks'},
]

/** The narrow navigation bar on the left. */
export function Rail() {
    const {session} = useSession()
    const {status} = useWorkspace()
    const counts = {changes: status?.changes.length ?? 0}

    return (
        <nav className="rail" aria-label="Main navigation">
            {sections.map(item => {
                const badge = item.badge?.(counts) ?? 0
                return (
                    <NavLink key={item.path} to={item.path} title={item.label} className={({isActive}) => (isActive ? 'active' : '')}>
                        <Icon name={item.icon}/>
                        <span>{item.label}</span>
                        {badge > 0 && <i className="rail-badge">{badge > 999 ? '999+' : badge}</i>}
                    </NavLink>
                )
            })}
            <div className="rail-spacer"/>
            <NavLink to="/account" title={session?.loggedIn ? `${session.username} on ${session.server}` : 'Log in'} className={({isActive}) => (isActive ? 'active' : '')}>
                {session?.username ? <Avatar person={{name: session.username}}/> : <Icon name="user"/>}
                <span>{session?.loggedIn ? 'Account' : 'Log in'}</span>
            </NavLink>
        </nav>
    )
}
