import {NavLink} from 'react-router'
import {Avatar} from '../../components/Avatar'
import {Icon, type IconName} from '../../components/Icon'
import {useSession} from '../session'

const sections: ReadonlyArray<{path: string; icon: IconName; label: string}> = [
    {path: '/workspace', icon: 'code', label: 'Workspace'},
    {path: '/history', icon: 'history', label: 'History'},
    {path: '/tasks', icon: 'tasks', label: 'Tasks'},
    {path: '/bugs', icon: 'bug', label: 'Bugs'},
    {path: '/graph', icon: 'graph', label: 'Graph'},
]

/** The narrow navigation bar on the left. */
export function Rail() {
    const {session} = useSession()

    return (
        <nav className="rail" aria-label="Main navigation">
            <div className="vertex-mark">
                <span>V</span>
            </div>
            {sections.map(item => (
                <NavLink key={item.path} to={item.path} title={item.label} className={({isActive}) => (isActive ? 'active' : '')}>
                    <Icon name={item.icon}/>
                    <span>{item.label}</span>
                </NavLink>
            ))}
            <div className="rail-spacer"/>
            <NavLink to="/account" title={session?.loggedIn ? `${session.username} on ${session.server}` : 'Log in'} className={({isActive}) => (isActive ? 'active' : '')}>
                {session?.username ? <Avatar person={{name: session.username}}/> : <Icon name="user"/>}
                <span>{session?.loggedIn ? 'Account' : 'Log in'}</span>
            </NavLink>
        </nav>
    )
}
